using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

var instancesOnly = HasFlag(args, "--instances-only");
var playerStatsOnly = HasFlag(args, "--player-stats");
var inventorySlotsOnly = HasFlag(args, "--inventory-slots");
var playerDebugOnly = HasFlag(args, "--player-debug");
var playerFloatToFind = TryGetFloatOptionValue(args, "--find-player-float");
var typeToDump = GetOptionValue(args, "--dump-type");
var enumToDump = GetOptionValue(args, "--dump-enum");
var trainerOptions = ParseTrainerOptions(args);
if (!trainerOptions.IsValid)
{
    return 2;
}

var processId = ResolveProcessId(args);
if (processId is null)
{
    Console.Error.WriteLine("Everwind is not running.");
    Console.Error.WriteLine(@"Start the game normally with %USERPROFILE%\Documents\games\Everwind\Everwind.exe, then run this probe again.");
    return 2;
}

using var process = Process.GetProcessById(processId.Value);
var module = process.MainModule ?? throw new InvalidOperationException("The process has no main module.");
var moduleBase = (ulong)module.BaseAddress.ToInt64();
var executable = module.FileName;

using var handle = NativeMethods.OpenProcess(
    NativeMethods.ProcessVmRead |
    NativeMethods.ProcessQueryInformation |
    (trainerOptions.Enabled ? NativeMethods.ProcessVmWrite | NativeMethods.ProcessVmOperation : 0),
    false,
    processId.Value);
if (handle.IsInvalid)
{
    throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcess failed.");
}

using var file = File.OpenRead(executable);
using var pe = new PEReader(file);
var dataSection = pe.PEHeaders.SectionHeaders.FirstOrDefault(section => section.Name == ".data");
if (dataSection.Name != ".data")
{
    throw new InvalidDataException("The executable has no .data section.");
}

var dataAddress = moduleBase + (uint)dataSection.VirtualAddress;
var dataSize = Math.Max(dataSection.VirtualSize, dataSection.SizeOfRawData);
var data = ReadMemory(handle, dataAddress, dataSize);
var candidates = new List<Candidate>();

for (var offset = 0; offset <= data.Length - 0x30; offset += 8)
{
    var objects = ReadUInt64(data, offset + 0x10);
    var maxElements = ReadInt32(data, offset + 0x20);
    var numElements = ReadInt32(data, offset + 0x24);
    var maxChunks = ReadInt32(data, offset + 0x28);
    var numChunks = ReadInt32(data, offset + 0x2C);

    if (!IsPointer(objects) ||
        numElements < 1_000 ||
        maxElements < numElements ||
        maxElements > 50_000_000 ||
        numChunks <= 0 ||
        maxChunks < numChunks ||
        maxChunks > 10_000 ||
        (long)maxChunks * 65_536 < maxElements ||
        (numElements + 65_535) / 65_536 != numChunks)
    {
        continue;
    }

    byte[] chunkTable;
    try
    {
        chunkTable = ReadMemory(handle, objects, checked(numChunks * 8));
    }
    catch (Win32Exception)
    {
        continue;
    }

    var chunks = new ulong[numChunks];
    var chunksValid = true;
    for (var index = 0; index < chunks.Length; index++)
    {
        chunks[index] = ReadUInt64(chunkTable, index * 8);
        chunksValid &= IsPointer(chunks[index]);
    }

    if (!chunksValid)
    {
        continue;
    }

    var checkedObjects = 0;
    var matchingIndices = 0;
    for (var objectIndex = 0; objectIndex < Math.Min(numElements, 512); objectIndex++)
    {
        var chunk = chunks[objectIndex / 65_536];
        var itemAddress = chunk + (ulong)(objectIndex % 65_536) * 0x18;
        try
        {
            var item = ReadMemory(handle, itemAddress, 0x18);
            var objectAddress = ReadUInt64(item, 0);
            if (!IsPointer(objectAddress))
            {
                continue;
            }

            var unrealObject = ReadMemory(handle, objectAddress, 0x28);
            var internalIndex = ReadInt32(unrealObject, 0x0C);
            var classAddress = ReadUInt64(unrealObject, 0x10);
            checkedObjects++;
            if (internalIndex == objectIndex && IsPointer(classAddress))
            {
                matchingIndices++;
            }
        }
        catch (Win32Exception)
        {
            // Sparse slots and objects disappearing during the scan are normal.
        }
    }

    if (checkedObjects >= 16 && matchingIndices * 100 / checkedObjects >= 80)
    {
        candidates.Add(new Candidate(
            dataAddress + (uint)offset,
            (uint)dataSection.VirtualAddress + (uint)offset,
            objects,
            maxElements,
            numElements,
            maxChunks,
            numChunks,
            checkedObjects,
            matchingIndices));
    }
}

Console.WriteLine($"Process: {process.ProcessName} ({process.Id})");
Console.WriteLine($"Module base: 0x{moduleBase:X}");
Console.WriteLine($"Executable: {executable}");
Console.WriteLine($"GUObjectArray candidates: {candidates.Count}");
foreach (var candidate in candidates)
{
    Console.WriteLine(
        $"Address=0x{candidate.Address:X} RVA=0x{candidate.Rva:X} " +
        $"Objects=0x{candidate.Objects:X} Elements={candidate.NumElements}/{candidate.MaxElements} " +
        $"Chunks={candidate.NumChunks}/{candidate.MaxChunks} " +
        $"Verified={candidate.MatchingIndices}/{candidate.CheckedObjects}");
}

var namePoolCandidates = FindNamePools(handle, moduleBase, pe);
var nameBlockCandidates = new List<NameBlockCandidate>();
if (namePoolCandidates.Count == 0)
{
    nameBlockCandidates = FindNameBlocks(handle);
    namePoolCandidates = FindNamePoolsFromBlocks(handle, moduleBase, pe, nameBlockCandidates);
}

Console.WriteLine($"FNamePool candidates: {namePoolCandidates.Count}");
foreach (var candidate in namePoolCandidates)
{
    Console.WriteLine(
        $"Address=0x{candidate.Address:X} RVA=0x{candidate.Rva:X} " +
        $"Block={candidate.CurrentBlock} Cursor=0x{candidate.CurrentByteCursor:X} " +
        $"FirstNames=[{string.Join(", ", candidate.FirstNames)}]");
}

if (nameBlockCandidates.Count > 0)
{
    Console.WriteLine($"FName block candidates: {nameBlockCandidates.Count}");
    foreach (var block in nameBlockCandidates.Take(8))
    {
        Console.WriteLine($"Block=0x{block.Address:X} FirstNames=[{string.Join(", ", block.FirstNames)}]");
    }
}

if (candidates.Count == 1 && namePoolCandidates.Count >= 1)
{
    var namePool = new FNamePoolReader(handle, namePoolCandidates[0].Address);
    if (trainerOptions.Enabled)
    {
        return RunExternalTrainer(handle, candidates[0], namePool, trainerOptions);
    }

    if (!string.IsNullOrWhiteSpace(typeToDump))
    {
        Console.WriteLine();
        DumpTypeProperties(handle, candidates[0], namePool, typeToDump);
        return 0;
    }

    if (!string.IsNullOrWhiteSpace(enumToDump))
    {
        Console.WriteLine();
        DumpEnum(handle, candidates[0], namePool, enumToDump);
        return 0;
    }

    if (inventorySlotsOnly)
    {
        Console.WriteLine();
        DumpPlayerInventorySlots(handle, candidates[0], namePool);
        return 0;
    }

    if (playerDebugOnly || playerFloatToFind is not null)
    {
        Console.WriteLine();
        DumpPlayerDebug(handle, candidates[0], namePool, playerFloatToFind);
        return 0;
    }

    if (playerStatsOnly)
    {
        Console.WriteLine();
        DumpPlayerStatistics(handle, candidates[0], namePool);
        return 0;
    }

    if (!instancesOnly)
    {
        Console.WriteLine();
        WriteSectionTitle("Filtered UObject samples:");
        DumpMatchingObjects(handle, candidates[0], namePool);
        Console.WriteLine();
        WriteSectionTitle("Relevant Skyverse class properties:");
        DumpRelevantClassProperties(handle, candidates[0], namePool);
    }

    Console.WriteLine();
    WriteSectionTitle("Target live instances:");
    DumpTargetInstances(handle, candidates[0], namePool, liveOnly: instancesOnly);
}

return candidates.Count == 1 ? 0 : 1;

static int? ResolveProcessId(string[] args)
{
    int? explicitProcessId = null;
    for (var index = 0; index < args.Length; index++)
    {
        if (args[index].Equals("--pid", StringComparison.OrdinalIgnoreCase) &&
            index + 1 < args.Length &&
            int.TryParse(args[index + 1], out var parsedProcessId))
        {
            explicitProcessId = parsedProcessId;
            index++;
            continue;
        }

        if (args[index].Equals("--instances-only", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (args[index].Equals("--player-stats", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (args[index].Equals("--inventory-slots", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (args[index].Equals("--player-debug", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (args[index].Equals("--dump-type", StringComparison.OrdinalIgnoreCase) &&
            index + 1 < args.Length)
        {
            index++;
            continue;
        }

        if (args[index].Equals("--dump-enum", StringComparison.OrdinalIgnoreCase) &&
            index + 1 < args.Length)
        {
            index++;
            continue;
        }

        if (args[index].Equals("--find-player-float", StringComparison.OrdinalIgnoreCase) &&
            index + 1 < args.Length)
        {
            index++;
            continue;
        }

        if (args[index].Equals("--train", StringComparison.OrdinalIgnoreCase) ||
            args[index].Equals("--infinite-health", StringComparison.OrdinalIgnoreCase) ||
            args[index].Equals("--infinite-stamina", StringComparison.OrdinalIgnoreCase) ||
            args[index].Equals("--no-durability-loss", StringComparison.OrdinalIgnoreCase) ||
            args[index].Equals("--disable-infinite-health", StringComparison.OrdinalIgnoreCase) ||
            args[index].Equals("--disable-infinite-stamina", StringComparison.OrdinalIgnoreCase) ||
            args[index].Equals("--reset-damage", StringComparison.OrdinalIgnoreCase) ||
            args[index].Equals("--reset-block-damage", StringComparison.OrdinalIgnoreCase) ||
            args[index].Equals("--reset-xp", StringComparison.OrdinalIgnoreCase) ||
            args[index].Equals("--reset-speed", StringComparison.OrdinalIgnoreCase) ||
            args[index].Equals("--reset-jump", StringComparison.OrdinalIgnoreCase) ||
            args[index].Equals("--reset-durability", StringComparison.OrdinalIgnoreCase) ||
            args[index].Equals("--reset-player-defaults", StringComparison.OrdinalIgnoreCase) ||
            args[index].Equals("--reset-known-defaults", StringComparison.OrdinalIgnoreCase) ||
            args[index].Equals("--once", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (args[index].Equals("--damage", StringComparison.OrdinalIgnoreCase) ||
            args[index].Equals("--block-damage", StringComparison.OrdinalIgnoreCase) ||
            args[index].Equals("--xp", StringComparison.OrdinalIgnoreCase) ||
            args[index].Equals("--speed", StringComparison.OrdinalIgnoreCase) ||
            args[index].Equals("--jump", StringComparison.OrdinalIgnoreCase) ||
            args[index].Equals("--durability", StringComparison.OrdinalIgnoreCase) ||
            args[index].Equals("--item-amount", StringComparison.OrdinalIgnoreCase) ||
            args[index].Equals("--slot-amount", StringComparison.OrdinalIgnoreCase) ||
            args[index].Equals("--interval", StringComparison.OrdinalIgnoreCase))
        {
            index++;
            continue;
        }

        Console.Error.WriteLine("Usage: Everwind.RuntimeProbe [--pid <process-id>] [--instances-only] [--player-stats] [--inventory-slots] [--player-debug] [--find-player-float N] [--dump-type <name>] [--dump-enum <name>]");
        Console.Error.WriteLine("       Everwind.RuntimeProbe --train [--infinite-health] [--infinite-stamina] [--damage N] [--block-damage N] [--xp N] [--speed N] [--jump N] [--durability N] [--item-amount N] [--no-durability-loss] [--disable-infinite-health] [--disable-infinite-stamina] [--reset-damage] [--reset-block-damage] [--reset-xp] [--reset-speed] [--reset-jump] [--reset-durability] [--reset-player-defaults] [--interval MS] [--once]");
        return null;
    }

    if (explicitProcessId is not null)
    {
        return explicitProcessId.Value;
    }

    using var process = FindEverwindProcess();
    return process?.Id;
}

static bool HasFlag(string[] args, string flag) =>
    args.Any(arg => arg.Equals(flag, StringComparison.OrdinalIgnoreCase));

static string? GetOptionValue(string[] args, string option)
{
    for (var index = 0; index < args.Length - 1; index++)
    {
        if (args[index].Equals(option, StringComparison.OrdinalIgnoreCase))
        {
            return args[index + 1];
        }
    }

    return null;
}

static float? TryGetFloatOptionValue(string[] args, string option)
{
    var value = GetOptionValue(args, option);
    if (value is null)
    {
        return null;
    }

    return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
        ? parsed
        : null;
}

static TrainerOptions ParseTrainerOptions(string[] args)
{
    var options = new TrainerOptions();

    for (var index = 0; index < args.Length; index++)
    {
        var arg = args[index];
        if (arg.Equals("--train", StringComparison.OrdinalIgnoreCase))
        {
            options.Enabled = true;
        }
        else if (arg.Equals("--infinite-health", StringComparison.OrdinalIgnoreCase))
        {
            options.InfiniteHealth = true;
        }
        else if (arg.Equals("--infinite-stamina", StringComparison.OrdinalIgnoreCase))
        {
            options.InfiniteStamina = true;
        }
        else if (arg.Equals("--no-durability-loss", StringComparison.OrdinalIgnoreCase))
        {
            options.NoDurabilityLoss = true;
        }
        else if (arg.Equals("--disable-infinite-health", StringComparison.OrdinalIgnoreCase))
        {
            options.DisableInfiniteHealth = true;
        }
        else if (arg.Equals("--disable-infinite-stamina", StringComparison.OrdinalIgnoreCase))
        {
            options.DisableInfiniteStamina = true;
        }
        else if (arg.Equals("--reset-damage", StringComparison.OrdinalIgnoreCase))
        {
            options.ResetDamageMultiplier = true;
        }
        else if (arg.Equals("--reset-block-damage", StringComparison.OrdinalIgnoreCase))
        {
            options.ResetBlockDamageMultiplier = true;
        }
        else if (arg.Equals("--reset-xp", StringComparison.OrdinalIgnoreCase))
        {
            options.ResetExperienceMultiplier = true;
        }
        else if (arg.Equals("--reset-speed", StringComparison.OrdinalIgnoreCase))
        {
            options.ResetSpeedMultiplier = true;
        }
        else if (arg.Equals("--reset-jump", StringComparison.OrdinalIgnoreCase))
        {
            options.ResetJumpMultiplier = true;
        }
        else if (arg.Equals("--reset-durability", StringComparison.OrdinalIgnoreCase))
        {
            options.ResetDurabilityUsageMultiplier = true;
        }
        else if (arg.Equals("--reset-player-defaults", StringComparison.OrdinalIgnoreCase) ||
                 arg.Equals("--reset-known-defaults", StringComparison.OrdinalIgnoreCase))
        {
            options.ResetPlayerDefaults = true;
        }
        else if (arg.Equals("--once", StringComparison.OrdinalIgnoreCase))
        {
            options.Once = true;
        }
        else if (TryParseFloatOption(args, ref index, "--damage", out var damage))
        {
            if (float.IsNaN(damage)) options.IsValid = false;
            else options.DamageMultiplier = damage;
        }
        else if (TryParseFloatOption(args, ref index, "--block-damage", out var blockDamage))
        {
            if (float.IsNaN(blockDamage)) options.IsValid = false;
            else options.BlockDamageMultiplier = blockDamage;
        }
        else if (TryParseFloatOption(args, ref index, "--xp", out var xp))
        {
            if (float.IsNaN(xp)) options.IsValid = false;
            else options.ExperienceMultiplier = xp;
        }
        else if (TryParseFloatOption(args, ref index, "--speed", out var speed))
        {
            if (float.IsNaN(speed)) options.IsValid = false;
            else options.SpeedMultiplier = speed;
        }
        else if (TryParseFloatOption(args, ref index, "--jump", out var jump))
        {
            if (float.IsNaN(jump)) options.IsValid = false;
            else options.JumpMultiplier = jump;
        }
        else if (TryParseFloatOption(args, ref index, "--durability", out var durability))
        {
            if (float.IsNaN(durability)) options.IsValid = false;
            else options.DurabilityUsageMultiplier = durability;
        }
        else if (TryParseIntOption(args, ref index, "--item-amount", out var itemAmount) ||
                 TryParseIntOption(args, ref index, "--slot-amount", out itemAmount))
        {
            if (itemAmount <= 0)
            {
                Console.Error.WriteLine("--item-amount/--slot-amount must be greater than zero.");
                options.IsValid = false;
            }
            else options.ItemAmount = itemAmount;
        }
        else if (TryParseIntOption(args, ref index, "--interval", out var interval))
        {
            if (interval <= 0)
            {
                Console.Error.WriteLine("--interval must be greater than zero.");
                options.IsValid = false;
            }
            else options.IntervalMs = Math.Clamp(interval, 50, 10_000);
        }
    }

    if (!options.Enabled && options.HasWriteFlags)
    {
        Console.Error.WriteLine("Trainer write flags require --train. Run without --train for read-only probing.");
        options.IsValid = false;
    }

    if (options.Enabled && !options.HasWriteFlags)
    {
        Console.Error.WriteLine("Trainer mode needs at least one feature flag.");
        options.IsValid = false;
    }

    return options;
}

static void WriteSectionTitle(string title) => Console.WriteLine(title);

static bool TryParseFloatOption(string[] args, ref int index, string option, out float value)
{
    value = 0;
    if (!args[index].Equals(option, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    if (index + 1 >= args.Length ||
        !float.TryParse(args[index + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
    {
        Console.Error.WriteLine($"Missing or invalid value for {option}.");
        value = float.NaN;
        return true;
    }

    index++;
    return true;
}

static bool TryParseIntOption(string[] args, ref int index, string option, out int value)
{
    value = 0;
    if (!args[index].Equals(option, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    if (index + 1 >= args.Length ||
        !int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
    {
        Console.Error.WriteLine($"Missing or invalid value for {option}.");
        value = -1;
        return true;
    }

    index++;
    return true;
}

static Process? FindEverwindProcess()
{
    var candidates = Process.GetProcesses()
        .Where(process =>
            process.ProcessName.Equals("Everwind-Win64-Shipping", StringComparison.OrdinalIgnoreCase) ||
            process.ProcessName.Equals("Everwind", StringComparison.OrdinalIgnoreCase))
        .Select(process =>
        {
            try
            {
                var modulePath = process.MainModule?.FileName ?? "";
                var isShipping = modulePath.EndsWith(
                    @"skyverse\Binaries\Win64\Everwind-Win64-Shipping.exe",
                    StringComparison.OrdinalIgnoreCase);
                return new
                {
                    Process = process,
                    IsShipping = isShipping,
                    StartTime = TryGetStartTime(process),
                    ModulePath = modulePath
                };
            }
            catch (Win32Exception)
            {
                process.Dispose();
                return null;
            }
            catch (InvalidOperationException)
            {
                process.Dispose();
                return null;
            }
        })
        .Where(candidate => candidate is not null)
        .OrderByDescending(candidate => candidate!.IsShipping)
        .ThenByDescending(candidate => candidate!.StartTime)
        .ToList();

    foreach (var candidate in candidates.Skip(1))
    {
        candidate!.Process.Dispose();
    }

    return candidates.FirstOrDefault()?.Process;
}

static DateTime TryGetStartTime(Process process)
{
    try
    {
        return process.StartTime;
    }
    catch (Win32Exception)
    {
        return DateTime.MinValue;
    }
    catch (InvalidOperationException)
    {
        return DateTime.MinValue;
    }
}

static bool IsPointer(ulong value) => value >= 0x1_0000 && value < 0x0000_8000_0000_0000;

static int ReadInt32(byte[] bytes, int offset) => BitConverter.ToInt32(bytes, offset);

static ushort ReadUInt16(byte[] bytes, int offset) => BitConverter.ToUInt16(bytes, offset);

static ulong ReadUInt64(byte[] bytes, int offset) => BitConverter.ToUInt64(bytes, offset);

static List<NamePoolCandidate> FindNamePools(SafeProcessHandle process, ulong moduleBase, PEReader pe)
{
    var results = new List<NamePoolCandidate>();

    foreach (var section in pe.PEHeaders.SectionHeaders.Where(section =>
                 section.Name is ".data" or ".rdata"))
    {
        var sectionAddress = moduleBase + (uint)section.VirtualAddress;
        var sectionSize = Math.Max(section.VirtualSize, section.SizeOfRawData);
        byte[] sectionBytes;
        try
        {
            sectionBytes = ReadMemory(process, sectionAddress, sectionSize);
        }
        catch (Win32Exception)
        {
            continue;
        }

        for (var offset = 0; offset <= sectionBytes.Length - 0x40; offset += 8)
        {
            var currentBlock = ReadInt32(sectionBytes, offset + 0x08);
            var currentByteCursor = ReadInt32(sectionBytes, offset + 0x0C);
            var block0 = ReadUInt64(sectionBytes, offset + 0x10);

            if (currentBlock < 0 ||
                currentBlock > 8_192 ||
                currentByteCursor <= 0 ||
                currentByteCursor > 0x20000 ||
                !IsPointer(block0))
            {
                continue;
            }

            byte[] blockBytes;
            try
            {
                blockBytes = ReadMemory(process, block0, 512);
            }
            catch (Win32Exception)
            {
                continue;
            }

            var firstNames = DecodeSequentialNames(blockBytes, 8).ToArray();
            if (firstNames.Length < 4 ||
                !firstNames[0].Equals("None", StringComparison.Ordinal) ||
                !firstNames.Any(name => name.Contains("Property", StringComparison.Ordinal)))
            {
                continue;
            }

            results.Add(new NamePoolCandidate(
                sectionAddress + (uint)offset,
                (uint)section.VirtualAddress + (uint)offset,
                currentBlock,
                currentByteCursor,
                firstNames));
        }
    }

    return results;
}

static List<NamePoolCandidate> FindNamePoolsFromBlocks(
    SafeProcessHandle process,
    ulong moduleBase,
    PEReader pe,
    IReadOnlyCollection<NameBlockCandidate> blocks)
{
    var results = new List<NamePoolCandidate>();
    if (blocks.Count == 0)
    {
        return results;
    }

    foreach (var section in pe.PEHeaders.SectionHeaders.Where(section =>
                 section.Name is ".data" or ".rdata"))
    {
        var sectionAddress = moduleBase + (uint)section.VirtualAddress;
        var sectionSize = Math.Max(section.VirtualSize, section.SizeOfRawData);
        byte[] sectionBytes;
        try
        {
            sectionBytes = ReadMemory(process, sectionAddress, sectionSize);
        }
        catch (Win32Exception)
        {
            continue;
        }

        foreach (var block in blocks)
        {
            var pointerBytes = BitConverter.GetBytes(block.Address);
            foreach (var pointerOffset in FindAll(sectionBytes, pointerBytes))
            {
                if (pointerOffset < 0x10)
                {
                    continue;
                }

                var currentBlock = ReadInt32(sectionBytes, pointerOffset - 0x08);
                var currentByteCursor = ReadInt32(sectionBytes, pointerOffset - 0x04);
                if (currentBlock < 0 ||
                    currentBlock > 8_192 ||
                    currentByteCursor <= 0 ||
                    currentByteCursor > 0x20000)
                {
                    continue;
                }

                var address = sectionAddress + (uint)(pointerOffset - 0x10);
                if (results.Any(existing => existing.Address == address))
                {
                    continue;
                }

                results.Add(new NamePoolCandidate(
                    address,
                    (uint)section.VirtualAddress + (uint)(pointerOffset - 0x10),
                    currentBlock,
                    currentByteCursor,
                    block.FirstNames));
            }
        }
    }

    return results;
}

static List<NameBlockCandidate> FindNameBlocks(SafeProcessHandle process)
{
    var results = new List<NameBlockCandidate>();
    var pattern = "None"u8.ToArray();
    var address = 0UL;
    var mbiSize = (nuint)Marshal.SizeOf<NativeMethods.MemoryBasicInformation>();

    while (address < 0x0000_8000_0000_0000 && results.Count < 32)
    {
        var queried = NativeMethods.VirtualQueryEx(
            process,
            (nint)address,
            out var memory,
            mbiSize);
        if (queried == 0)
        {
            break;
        }

        var baseAddress = (ulong)memory.BaseAddress;
        var regionSize = (ulong)memory.RegionSize;
        var nextAddress = baseAddress + regionSize;
        if (nextAddress <= address)
        {
            break;
        }

        address = nextAddress;

        if (memory.State != NativeMethods.MemCommit ||
            !IsReadableProtection(memory.Protect) ||
            regionSize == 0 ||
            regionSize > 256UL * 1024 * 1024)
        {
            continue;
        }

        var maxReadable = (int)Math.Min(regionSize, 32UL * 1024 * 1024);
        byte[] bytes;
        try
        {
            bytes = ReadMemory(process, baseAddress, maxReadable);
        }
        catch (Win32Exception)
        {
            continue;
        }

        foreach (var textOffset in FindAll(bytes, pattern))
        {
            if (textOffset < 2)
            {
                continue;
            }

            var offset = textOffset - 2;
            var firstNames = DecodeSequentialNames(bytes.AsSpan(offset).ToArray(), 12).ToArray();
            if (firstNames.Length < 4 ||
                !firstNames[0].Equals("None", StringComparison.Ordinal) ||
                !firstNames.Any(name => name.Equals("ByteProperty", StringComparison.Ordinal)) ||
                !firstNames.Any(name => name.Equals("IntProperty", StringComparison.Ordinal)))
            {
                continue;
            }

            var candidateAddress = baseAddress + (uint)offset;
            if (results.Any(existing => existing.Address == candidateAddress))
            {
                continue;
            }

            results.Add(new NameBlockCandidate(candidateAddress, firstNames));
        }
    }

    return results;
}

static bool IsReadableProtection(uint protection)
{
    if ((protection & NativeMethods.PageGuard) != 0 ||
        (protection & NativeMethods.PageNoAccess) != 0)
    {
        return false;
    }

    var basicProtection = protection & 0xFF;
    return basicProtection is
        NativeMethods.PageReadonly or
        NativeMethods.PageReadwrite or
        NativeMethods.PageWritecopy or
        NativeMethods.PageExecuteRead or
        NativeMethods.PageExecuteReadwrite or
        NativeMethods.PageExecuteWritecopy;
}

static IEnumerable<int> FindAll(byte[] haystack, byte[] needle)
{
    for (var index = 0; index <= haystack.Length - needle.Length; index++)
    {
        if (haystack.AsSpan(index, needle.Length).SequenceEqual(needle))
        {
            yield return index;
        }
    }
}

static IEnumerable<string> DecodeSequentialNames(byte[] blockBytes, int maxNames)
{
    var offset = 0;
    for (var index = 0; index < maxNames && offset + 2 < blockBytes.Length; index++)
    {
        if (!TryDecodeFNameEntry(blockBytes, offset, out var name, out var entrySize))
        {
            yield break;
        }

        yield return name;
        offset += entrySize;
    }
}

static bool TryDecodeFNameEntry(byte[] bytes, int offset, out string name, out int entrySize)
{
    name = "";
    entrySize = 0;

    if (offset + 2 > bytes.Length)
    {
        return false;
    }

    var header = ReadUInt16(bytes, offset);
    return TryDecodeFNameEntryWithLength(bytes, offset, (header & 1) != 0, header >> 1, out name, out entrySize) ||
           TryDecodeFNameEntryWithLength(bytes, offset, (header & 1) != 0, header >> 6, out name, out entrySize);
}

static bool TryDecodeFNameEntryWithLength(
    byte[] bytes,
    int offset,
    bool isWide,
    int length,
    out string name,
    out int entrySize)
{
    name = "";
    entrySize = 0;

    if (length <= 0 || length > 1_024)
    {
        return false;
    }

    var byteLength = isWide ? checked(length * 2) : length;
    var textOffset = offset + 2;
    if (textOffset + byteLength > bytes.Length)
    {
        return false;
    }

    name = isWide
        ? Encoding.Unicode.GetString(bytes, textOffset, byteLength)
        : Encoding.ASCII.GetString(bytes, textOffset, byteLength);
    if (!IsPlausibleFName(name))
    {
        return false;
    }

    entrySize = Align(textOffset + byteLength - offset, 2);
    return true;
}

static bool IsPlausibleFName(string name) =>
    name.Length > 0 &&
    name.All(character => !char.IsControl(character) && character != '\uFFFD');

static int Align(int value, int alignment) => (value + alignment - 1) / alignment * alignment;

static void DumpMatchingObjects(SafeProcessHandle process, Candidate objectArray, FNamePoolReader names)
{
    var keywords = new[]
    {
        "Skyverse",
        "Player",
        "Character",
        "Health",
        "Stamina",
        "Damage",
        "Block",
        "Inventory",
        "Item",
        "Durability",
        "Fuel",
        "Craft",
        "Experience",
        "Movement",
        "Jump"
    };

    byte[] chunkTable;
    try
    {
        chunkTable = ReadMemory(process, objectArray.Objects, checked(objectArray.NumChunks * 8));
    }
    catch (Win32Exception exception)
    {
        Console.WriteLine($"  Unable to read GUObjectArray chunks: {exception.Message}");
        return;
    }

    var chunks = new ulong[objectArray.NumChunks];
    for (var index = 0; index < chunks.Length; index++)
    {
        chunks[index] = ReadUInt64(chunkTable, index * 8);
    }

    var classNameCache = new Dictionary<ulong, string>();
    var outerNameCache = new Dictionary<ulong, string>();
    var printed = 0;
    var inspected = 0;

    for (var objectIndex = 0; objectIndex < objectArray.NumElements && printed < 250; objectIndex++)
    {
        var chunk = chunks[objectIndex / 65_536];
        if (!IsPointer(chunk))
        {
            continue;
        }

        var itemAddress = chunk + (ulong)(objectIndex % 65_536) * 0x18;
        byte[] item;
        try
        {
            item = ReadMemory(process, itemAddress, 0x18);
        }
        catch (Win32Exception)
        {
            continue;
        }

        var objectAddress = ReadUInt64(item, 0);
        if (!IsPointer(objectAddress))
        {
            continue;
        }

        byte[] unrealObject;
        try
        {
            unrealObject = ReadMemory(process, objectAddress, 0x28);
        }
        catch (Win32Exception)
        {
            continue;
        }

        inspected++;
        var internalIndex = ReadInt32(unrealObject, 0x0C);
        var classAddress = ReadUInt64(unrealObject, 0x10);
        var nameIndex = ReadInt32(unrealObject, 0x18);
        var nameNumber = ReadInt32(unrealObject, 0x1C);
        var outerAddress = ReadUInt64(unrealObject, 0x20);

        var objectName = FormatUObjectName(names.Resolve(nameIndex), nameNumber);
        var className = ResolveObjectName(process, names, classAddress, classNameCache);
        var outerName = ResolveObjectName(process, names, outerAddress, outerNameCache);

        if (!MatchesAny(objectName, keywords) &&
            !MatchesAny(className, keywords) &&
            !MatchesAny(outerName, keywords))
        {
            continue;
        }

        Console.WriteLine(
            $"  [{internalIndex,6}] 0x{objectAddress:X} Class={className} Name={objectName} Outer={outerName}");
        printed++;
    }

    Console.WriteLine($"  inspected={inspected} printed={printed}");
}

static void DumpRelevantClassProperties(SafeProcessHandle process, Candidate objectArray, FNamePoolReader names)
{
    var classKeywords = new[]
    {
        "Character",
        "Statistic",
        "Status",
        "Health",
        "Stamina",
        "Damage",
        "Inventory",
        "Item",
        "Durability",
        "Fuel",
        "Craft",
        "Equipment",
        "Movement",
        "Glider",
        "Block",
        "Tool",
        "Energy"
    };
    var propertyKeywords = new[]
    {
        "Health",
        "Stamina",
        "Damage",
        "Inventory",
        "Item",
        "Durability",
        "Fuel",
        "Craft",
        "Experience",
        "XP",
        "Move",
        "Movement",
        "Speed",
        "Jump",
        "Current",
        "Max",
        "Value",
        "Amount",
        "Energy",
        "Consume",
        "Cost",
        "Regen"
    };
    var dumpAllForClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "BaseCharacter",
        "AbstractBaseCharacter",
        "CharacterStatisticsComponent",
        "CharacterStatusesComponent",
        "CharStatRegenComponent",
        "DamageManagerComponent",
        "CombatComponent",
        "InventoryComponent",
        "InventorySlot",
        "CraftingInventoryComponent",
        "AlchemyInventoryComponent",
        "EnergyChargingInventoryComponent",
        "EquipmentComponent",
        "BaseStatistic",
        "BaseStatisticDataAsset",
        "BaseCharacterStatus",
        "CharacterStatusDataAsset",
        "ItemDataAsset",
        "ToolDataAsset",
        "BaseToolAction",
        "GliderMovementComponent"
    };

    byte[] chunkTable;
    try
    {
        chunkTable = ReadMemory(process, objectArray.Objects, checked(objectArray.NumChunks * 8));
    }
    catch (Win32Exception exception)
    {
        Console.WriteLine($"  Unable to read GUObjectArray chunks: {exception.Message}");
        return;
    }

    var chunks = new ulong[objectArray.NumChunks];
    for (var index = 0; index < chunks.Length; index++)
    {
        chunks[index] = ReadUInt64(chunkTable, index * 8);
    }

    var nameCache = new Dictionary<ulong, string>();
    var printedClasses = 0;
    for (var objectIndex = 0; objectIndex < objectArray.NumElements && printedClasses < 80; objectIndex++)
    {
        var chunk = chunks[objectIndex / 65_536];
        if (!IsPointer(chunk))
        {
            continue;
        }

        var itemAddress = chunk + (ulong)(objectIndex % 65_536) * 0x18;
        byte[] item;
        try
        {
            item = ReadMemory(process, itemAddress, 0x18);
        }
        catch (Win32Exception)
        {
            continue;
        }

        var objectAddress = ReadUInt64(item, 0);
        if (!IsPointer(objectAddress))
        {
            continue;
        }

        byte[] unrealObject;
        try
        {
            unrealObject = ReadMemory(process, objectAddress, 0x28);
        }
        catch (Win32Exception)
        {
            continue;
        }

        var classAddress = ReadUInt64(unrealObject, 0x10);
        var objectName = FormatUObjectName(names.Resolve(ReadInt32(unrealObject, 0x18)), ReadInt32(unrealObject, 0x1C));
        var objectClassName = ResolveObjectName(process, names, classAddress, nameCache);
        var outerName = ResolveObjectName(process, names, ReadUInt64(unrealObject, 0x20), nameCache);

        if (!objectClassName.Equals("Class", StringComparison.Ordinal) ||
            !outerName.Equals("/Script/Skyverse", StringComparison.Ordinal) ||
            !MatchesAny(objectName, classKeywords))
        {
            continue;
        }

        var properties = ReadStructProperties(process, names, objectAddress, nameCache, includeSuper: true);
        var selected = dumpAllForClasses.Contains(objectName)
            ? properties
            : properties.Where(property => MatchesAny(property.Name, propertyKeywords)).ToList();

        if (selected.Count == 0)
        {
            continue;
        }

        Console.WriteLine($"  {objectName} @ 0x{objectAddress:X}");
        foreach (var property in selected.Take(120))
        {
            Console.WriteLine(
                $"    +0x{property.Offset:X4} {property.Name,-40} size=0x{property.ElementSize:X} dim={property.ArrayDim} owner={property.Owner}");
        }

        printedClasses++;
    }

    Console.WriteLine($"  classes_printed={printedClasses}");
}

static void DumpTargetInstances(SafeProcessHandle process, Candidate objectArray, FNamePoolReader names, bool liveOnly)
{
    var targetClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "BaseCharacter",
        "CharacterStatisticsComponent",
        "BaseStatistic",
        "InventoryComponent",
        "CraftingInventoryComponent",
        "AlchemyInventoryComponent",
        "EnergyChargingInventoryComponent",
        "EquipmentComponent",
        "DamageManagerComponent",
        "BaseEnergyGeneratorBlockAction",
        "GliderMovementComponent"
    };

    byte[] chunkTable;
    try
    {
        chunkTable = ReadMemory(process, objectArray.Objects, checked(objectArray.NumChunks * 8));
    }
    catch (Win32Exception exception)
    {
        Console.WriteLine($"  Unable to read GUObjectArray chunks: {exception.Message}");
        return;
    }

    var chunks = new ulong[objectArray.NumChunks];
    for (var index = 0; index < chunks.Length; index++)
    {
        chunks[index] = ReadUInt64(chunkTable, index * 8);
    }

    var nameCache = new Dictionary<ulong, string>();
    var printed = 0;
    var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    for (var objectIndex = 0; objectIndex < objectArray.NumElements && printed < 220; objectIndex++)
    {
        var chunk = chunks[objectIndex / 65_536];
        if (!IsPointer(chunk))
        {
            continue;
        }

        var itemAddress = chunk + (ulong)(objectIndex % 65_536) * 0x18;
        byte[] item;
        try
        {
            item = ReadMemory(process, itemAddress, 0x18);
        }
        catch (Win32Exception)
        {
            continue;
        }

        var objectAddress = ReadUInt64(item, 0);
        if (!IsPointer(objectAddress))
        {
            continue;
        }

        byte[] unrealObject;
        try
        {
            unrealObject = ReadMemory(process, objectAddress, 0x28);
        }
        catch (Win32Exception)
        {
            continue;
        }

        var className = ResolveObjectName(process, names, ReadUInt64(unrealObject, 0x10), nameCache);
        if (!targetClasses.Contains(className))
        {
            continue;
        }

        counts.TryGetValue(className, out var count);
        counts[className] = count + 1;

        var objectName = FormatUObjectName(names.Resolve(ReadInt32(unrealObject, 0x18)), ReadInt32(unrealObject, 0x1C));
        var outerName = ResolveObjectName(process, names, ReadUInt64(unrealObject, 0x20), nameCache);
        var isDefault = objectName.StartsWith("Default__", StringComparison.OrdinalIgnoreCase) ||
                        outerName.StartsWith("Default__", StringComparison.OrdinalIgnoreCase);
        if (liveOnly && isDefault)
        {
            continue;
        }

        Console.WriteLine(
            $"  0x{objectAddress:X} Class={className,-32} Name={objectName,-45} Outer={outerName} Default={isDefault}");

        if (className.Equals("CharacterStatisticsComponent", StringComparison.OrdinalIgnoreCase))
        {
            DumpCharacterStatisticsValues(process, objectAddress, indent: "    ");
        }
        else if (className.EndsWith("InventoryComponent", StringComparison.OrdinalIgnoreCase))
        {
            DumpInventoryValues(process, objectAddress, className, indent: "    ");
        }
        else if (className.Equals("BaseStatistic", StringComparison.OrdinalIgnoreCase))
        {
            DumpBaseStatisticValue(process, objectAddress, "value", indent: "    ");
        }

        printed++;
    }

    Console.WriteLine("  counts:");
    foreach (var pair in counts.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine($"    {pair.Key}: {pair.Value}");
    }
}

static void DumpCharacterStatisticsValues(SafeProcessHandle process, ulong componentAddress, string indent)
{
    var stats = new (string Name, int Offset)[]
    {
        ("Hp", 0x00F0),
        ("Stamina", 0x00F8),
        ("XPValue", 0x01F0),
        ("XPGain", 0x0120),
        ("WalkMaxSpeed", 0x0198),
        ("SprintMaxSpeed", 0x01A0),
        ("JumpBoost", 0x0170),
        ("JumpStaminaCost", 0x0168),
        ("SprintStaminaCostRate", 0x0180),
        ("ToolUseStaminaCostRate", 0x0188),
        ("SwimmingSprintStaminaCostRate", 0x0190),
        ("DmgMultiplier", 0x0230),
        ("BlockDmgMultiplier", 0x0240),
        ("DurablilityUsageMultiplier", 0x0248)
    };

    foreach (var stat in stats)
    {
        try
        {
            var pointerBytes = ReadMemory(process, componentAddress + (uint)stat.Offset, 8);
            var statAddress = ReadUInt64(pointerBytes, 0);
            if (!IsPointer(statAddress))
            {
                Console.WriteLine($"{indent}{stat.Name,-28} <null>");
                continue;
            }

            DumpBaseStatisticValue(process, statAddress, $"{stat.Name} @ +0x{stat.Offset:X}", indent);
        }
        catch (Win32Exception)
        {
            Console.WriteLine($"{indent}{stat.Name,-28} <unreadable>");
        }
    }
}

static void DumpBaseStatisticValue(SafeProcessHandle process, ulong statisticAddress, string label, string indent)
{
    try
    {
        var includeFreezeRule =
            label.StartsWith("Hp", StringComparison.OrdinalIgnoreCase) ||
            label.StartsWith("Stamina", StringComparison.OrdinalIgnoreCase);
        var bytes = ReadMemory(process, statisticAddress, includeFreezeRule ? 0xEA : 0xB8);
        var current = BitConverter.ToSingle(bytes, 0x28);
        var baseValue = BitConverter.ToSingle(bytes, 0x2C);
        var rangedCurrent = BitConverter.ToSingle(bytes, 0xB4);
        var rangedPart = IsSaneDisplayFloat(rangedCurrent)
            ? $" rangedCurrent={rangedCurrent:0.###}"
            : "";
        var freezePart = includeFreezeRule
            ? $" freezeRule={bytes[0xE9]}"
            : "";
        Console.WriteLine($"{indent}{label,-36} ptr=0x{statisticAddress:X} current={current:0.###} base={baseValue:0.###}{rangedPart}{freezePart}");
    }
    catch (Win32Exception)
    {
        Console.WriteLine($"{indent}{label,-36} ptr=0x{statisticAddress:X} <unreadable>");
    }
}

static bool IsSaneDisplayFloat(float value) =>
    !float.IsNaN(value) &&
    !float.IsInfinity(value) &&
    Math.Abs(value) <= 1_000_000f;

static void DumpInventoryValues(SafeProcessHandle process, ulong inventoryAddress, string className, string indent)
{
    try
    {
        var bytes = ReadMemory(process, inventoryAddress, 0x2A0);
        var maxSlots = ReadInt32(bytes, 0x178);
        var slotsData = ReadUInt64(bytes, 0x188);
        var slotsNum = ReadInt32(bytes, 0x190);
        var slotsMax = ReadInt32(bytes, 0x194);
        Console.WriteLine($"{indent}MaxSlots={maxSlots} SlotsData=0x{slotsData:X} Slots={slotsNum}/{slotsMax}");

        if (className.Equals("EnergyChargingInventoryComponent", StringComparison.OrdinalIgnoreCase))
        {
            var currentFuelConsume = BitConverter.ToSingle(bytes, 0x240);
            var fuelConsumptionPerH = BitConverter.ToSingle(bytes, 0x244);
            var currentFuel = ReadUInt64(bytes, 0x258);
            Console.WriteLine($"{indent}CurrentFuel=0x{currentFuel:X} CurrentFuelConsume={currentFuelConsume:0.###} FuelConsumptionPerH={fuelConsumptionPerH:0.###}");
        }
    }
    catch (Win32Exception)
    {
        Console.WriteLine($"{indent}<inventory unreadable>");
    }
}

static void DumpTypeProperties(
    SafeProcessHandle process,
    Candidate objectArray,
    FNamePoolReader names,
    string typeName)
{
    byte[] chunkTable;
    try
    {
        chunkTable = ReadMemory(process, objectArray.Objects, checked(objectArray.NumChunks * 8));
    }
    catch (Win32Exception exception)
    {
        Console.WriteLine($"Unable to read GUObjectArray chunks: {exception.Message}");
        return;
    }

    var chunks = new ulong[objectArray.NumChunks];
    for (var index = 0; index < chunks.Length; index++)
    {
        chunks[index] = ReadUInt64(chunkTable, index * 8);
    }

    var nameCache = new Dictionary<ulong, string>();
    var matches = 0;

    for (var objectIndex = 0; objectIndex < objectArray.NumElements; objectIndex++)
    {
        var chunk = chunks[objectIndex / 65_536];
        if (!IsPointer(chunk))
        {
            continue;
        }

        var itemAddress = chunk + (ulong)(objectIndex % 65_536) * 0x18;
        byte[] item;
        try
        {
            item = ReadMemory(process, itemAddress, 0x18);
        }
        catch (Win32Exception)
        {
            continue;
        }

        var objectAddress = ReadUInt64(item, 0);
        if (!IsPointer(objectAddress))
        {
            continue;
        }

        byte[] unrealObject;
        try
        {
            unrealObject = ReadMemory(process, objectAddress, 0x28);
        }
        catch (Win32Exception)
        {
            continue;
        }

        var objectName = FormatUObjectName(names.Resolve(ReadInt32(unrealObject, 0x18)), ReadInt32(unrealObject, 0x1C));
        if (!objectName.Equals(typeName, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var objectClassName = ResolveObjectName(process, names, ReadUInt64(unrealObject, 0x10), nameCache);
        var outerName = ResolveObjectName(process, names, ReadUInt64(unrealObject, 0x20), nameCache);
        var properties = ReadStructProperties(process, names, objectAddress, nameCache, includeSuper: true);

        Console.WriteLine($"{objectName} @ 0x{objectAddress:X} Class={objectClassName} Outer={outerName}");
        if (properties.Count == 0)
        {
            Console.WriteLine("  <no reflected properties>");
        }

        foreach (var property in properties)
        {
            var boolMask =
                property.BoolFieldSize > 0 &&
                property.BoolFieldSize <= 8 &&
                property.BoolByteOffset < property.BoolFieldSize &&
                property.BoolByteMask != 0 &&
                property.BoolFieldMask != 0
                    ? $" boolMask=0x{property.BoolByteMask:X2} fieldMask=0x{property.BoolFieldMask:X2} byteOffset={property.BoolByteOffset}"
                    : "";

            Console.WriteLine(
                $"  +0x{property.Offset:X4} {property.Name,-40} size=0x{property.ElementSize:X} dim={property.ArrayDim} owner={property.Owner}{boolMask}");
        }

        matches++;
    }

    Console.WriteLine($"types_matched={matches}");
}

static void DumpEnum(
    SafeProcessHandle process,
    Candidate objectArray,
    FNamePoolReader names,
    string enumName)
{
    byte[] chunkTable;
    try
    {
        chunkTable = ReadMemory(process, objectArray.Objects, checked(objectArray.NumChunks * 8));
    }
    catch (Win32Exception exception)
    {
        Console.WriteLine($"Unable to read GUObjectArray chunks: {exception.Message}");
        return;
    }

    var chunks = new ulong[objectArray.NumChunks];
    for (var index = 0; index < chunks.Length; index++)
    {
        chunks[index] = ReadUInt64(chunkTable, index * 8);
    }

    var nameCache = new Dictionary<ulong, string>();
    var matches = 0;

    for (var objectIndex = 0; objectIndex < objectArray.NumElements; objectIndex++)
    {
        var chunk = chunks[objectIndex / 65_536];
        if (!IsPointer(chunk))
        {
            continue;
        }

        var itemAddress = chunk + (ulong)(objectIndex % 65_536) * 0x18;
        byte[] item;
        try
        {
            item = ReadMemory(process, itemAddress, 0x18);
        }
        catch (Win32Exception)
        {
            continue;
        }

        var objectAddress = ReadUInt64(item, 0);
        if (!IsPointer(objectAddress))
        {
            continue;
        }

        byte[] unrealObject;
        try
        {
            unrealObject = ReadMemory(process, objectAddress, 0x58);
        }
        catch (Win32Exception)
        {
            continue;
        }

        var objectName = FormatUObjectName(names.Resolve(ReadInt32(unrealObject, 0x18)), ReadInt32(unrealObject, 0x1C));
        if (!objectName.Equals(enumName, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var objectClassName = ResolveObjectName(process, names, ReadUInt64(unrealObject, 0x10), nameCache);
        if (!objectClassName.Equals("Enum", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var outerName = ResolveObjectName(process, names, ReadUInt64(unrealObject, 0x20), nameCache);
        var namesData = ReadUInt64(unrealObject, 0x40);
        var namesNum = ReadInt32(unrealObject, 0x48);
        var namesMax = ReadInt32(unrealObject, 0x4C);

        Console.WriteLine($"{objectName} @ 0x{objectAddress:X} Class={objectClassName} Outer={outerName} Names={namesNum}/{namesMax}");
        matches++;

        if (!IsPointer(namesData) || namesNum < 0 || namesNum > 1_024)
        {
            Console.WriteLine("  <enum names unreadable>");
            continue;
        }

        byte[] enumNames;
        try
        {
            enumNames = ReadMemory(process, namesData, checked(namesNum * 0x10));
        }
        catch (Win32Exception exception)
        {
            Console.WriteLine($"  <enum names unreadable: {exception.Message}>");
            continue;
        }

        for (var index = 0; index < namesNum; index++)
        {
            var entryOffset = index * 0x10;
            var name = FormatUObjectName(names.Resolve(ReadInt32(enumNames, entryOffset)), ReadInt32(enumNames, entryOffset + 4));
            var value = BitConverter.ToInt64(enumNames, entryOffset + 8);
            Console.WriteLine($"  {value,3}: {name}");
        }
    }

    Console.WriteLine($"enums_matched={matches}");
}

static void DumpPlayerInventorySlots(
    SafeProcessHandle process,
    Candidate objectArray,
    FNamePoolReader names)
{
    var inventory = FindPlayerInventoryComponent(process, objectArray, names);
    if (inventory.Address == 0)
    {
        Console.Error.WriteLine("Could not find the live player InventoryComponent.");
        Console.Error.WriteLine("Load into a world as the player, then retry.");
        return;
    }

    var inventoryBytes = ReadMemory(process, inventory.Address, 0x198);
    var maxSlots = ReadInt32(inventoryBytes, 0x178);
    var slotsData = ReadUInt64(inventoryBytes, 0x188);
    var slotsNum = ReadInt32(inventoryBytes, 0x190);
    var slotsMax = ReadInt32(inventoryBytes, 0x194);

    Console.WriteLine($"Player inventory: 0x{inventory.Address:X} ({inventory.OuterName})");
    Console.WriteLine($"  MaxSlots={maxSlots} SlotsData=0x{slotsData:X} Slots={slotsNum}/{slotsMax}");
    if (!IsPointer(slotsData) || slotsNum <= 0 || slotsNum > 10_000)
    {
        return;
    }

    var nameCache = new Dictionary<ulong, string>();
    var stride = DetectInventorySlotPointerStride(process, names, nameCache, slotsData, slotsNum);
    Console.WriteLine($"  SlotPointerStride=0x{stride:X}");

    var printed = 0;
    for (var index = 0; index < slotsNum; index++)
    {
        ulong slotAddress;
        try
        {
            slotAddress = ReadUInt64(ReadMemory(process, slotsData + (ulong)(index * stride), 8), 0);
        }
        catch (Win32Exception)
        {
            continue;
        }

        if (!IsPointer(slotAddress) || !IsUObjectClass(process, names, nameCache, slotAddress, "InventorySlot"))
        {
            continue;
        }

        byte[] slotBytes;
        try
        {
            slotBytes = ReadMemory(process, slotAddress, 0x58);
        }
        catch (Win32Exception)
        {
            continue;
        }

        var itemAddress = ReadUInt64(slotBytes, 0x48);
        var amount = ReadInt32(slotBytes, 0x50);
        if (!IsPointer(itemAddress) && amount == 0)
        {
            continue;
        }

        var itemName = ResolveObjectName(process, names, itemAddress, nameCache);
        var itemClass = ResolveObjectClassName(process, names, itemAddress, nameCache);
        Console.WriteLine(
            $"  [{index,2}] Slot=0x{slotAddress:X} Amount={amount,5} Item=0x{itemAddress:X} {itemName} ({itemClass})");
        printed++;
    }

    Console.WriteLine($"  non_empty_or_amount_slots={printed}");
}

static void DumpPlayerStatistics(
    SafeProcessHandle process,
    Candidate objectArray,
    FNamePoolReader names)
{
    var playerStats = FindPlayerStatisticsComponent(process, objectArray, names);
    if (playerStats.Address == 0)
    {
        Console.Error.WriteLine("Could not find the live player CharacterStatisticsComponent.");
        Console.Error.WriteLine("Load into a world as the player, then retry.");
        return;
    }

    Console.WriteLine($"Player stats: 0x{playerStats.Address:X} ({playerStats.OuterName})");
    DumpCharacterStatisticsValues(process, playerStats.Address, indent: "  ");
}

static void DumpPlayerDebug(
    SafeProcessHandle process,
    Candidate objectArray,
    FNamePoolReader names,
    float? floatToFind)
{
    var playerStats = FindPlayerStatisticsComponent(process, objectArray, names);
    if (playerStats.Address == 0 || playerStats.ActorAddress == 0)
    {
        Console.Error.WriteLine("Could not find the live player actor/stat component.");
        return;
    }

    var nameCache = new Dictionary<ulong, string>();
    Console.WriteLine($"Player actor: 0x{playerStats.ActorAddress:X} ({playerStats.OuterName})");
    Console.WriteLine($"Player stats: 0x{playerStats.Address:X}");
    var actorFlags = ReadMemory(process, playerStats.ActorAddress + 0x005A, 1)[0];
    Console.WriteLine($"Actor flags +0x5A: 0x{actorFlags:X2} bCanBeDamaged={(actorFlags & 0x04) != 0}");
    DumpKnownPlayerPointers(process, names, nameCache, playerStats.ActorAddress);

    Console.WriteLine();
    Console.WriteLine("Player stats values:");
    DumpCharacterStatisticsValues(process, playerStats.Address, indent: "  ");

    Console.WriteLine();
    DumpComponentFloatSummary(process, names, nameCache, "Actor", playerStats.ActorAddress, 0x1000, floatToFind);

    var actorBytes = ReadMemory(process, playerStats.ActorAddress, 0x910);
    var componentPointers = new (string Name, ulong Address, int Length)[]
    {
        ("CharacterStatisticsComponent", ReadUInt64(actorBytes, 0x740), 0x320),
        ("CharacterHPRegeneratorComponent", ReadUInt64(actorBytes, 0x748), 0x120),
        ("CharacterStaminaRegeneratorComponent", ReadUInt64(actorBytes, 0x750), 0x120),
        ("CharacterOxygenRegeneratorComponent", ReadUInt64(actorBytes, 0x758), 0x120),
        ("StatusesComponent", ReadUInt64(actorBytes, 0x760), 0x120),
        ("DamageManager", ReadUInt64(actorBytes, 0x7D0), 0x200),
        ("InventoryComponent", ReadUInt64(actorBytes, 0x900), 0x220)
    };

    foreach (var component in componentPointers)
    {
        DumpComponentFloatSummary(process, names, nameCache, component.Name, component.Address, component.Length, floatToFind);
    }

    var damageManager = componentPointers.First(component => component.Name == "DamageManager").Address;
    if (IsPointer(damageManager))
    {
        try
        {
            var damageBytes = ReadMemory(process, damageManager, 0xD8);
            var damageData = ReadUInt64(damageBytes, 0xA0);
            DumpComponentFloatSummary(process, names, nameCache, "DamageManager.Data", damageData, 0x300, floatToFind);
            DumpInlineStructFloats("DamageManager.DefaultDamageData", damageManager + 0xA8, damageBytes, 0xA8, 0x30, floatToFind);
        }
        catch (Win32Exception exception)
        {
            Console.WriteLine($"DamageManager extra debug failed: {exception.Message}");
        }
    }

    foreach (var stat in new[]
             {
                 ("Hp BaseStatistic", ReadStatisticPointer(process, playerStats.Address, 0x00F0)),
                 ("Stamina BaseStatistic", ReadStatisticPointer(process, playerStats.Address, 0x00F8))
             })
    {
        DumpComponentFloatSummary(process, names, nameCache, stat.Item1, stat.Item2, 0xB0, floatToFind);
    }
}

static void DumpKnownPlayerPointers(
    SafeProcessHandle process,
    FNamePoolReader names,
    Dictionary<ulong, string> nameCache,
    ulong actorAddress)
{
    var actorBytes = ReadMemory(process, actorAddress, 0x910);
    foreach (var pointer in new[]
             {
                 ("CharacterStatisticsComponent", 0x740),
                 ("CharacterHPRegeneratorComponent", 0x748),
                 ("CharacterStaminaRegeneratorComponent", 0x750),
                 ("CharacterOxygenRegeneratorComponent", 0x758),
                 ("StatusesComponent", 0x760),
                 ("DamageManager", 0x7D0),
                 ("InventoryComponent", 0x900)
             })
    {
        var address = ReadUInt64(actorBytes, pointer.Item2);
        Console.WriteLine(
            $"  +0x{pointer.Item2:X3} {pointer.Item1,-36} 0x{address:X} {ResolveObjectClassName(process, names, address, nameCache)}");
    }
}

static void DumpComponentFloatSummary(
    SafeProcessHandle process,
    FNamePoolReader names,
    Dictionary<ulong, string> nameCache,
    string label,
    ulong address,
    int length,
    float? floatToFind)
{
    if (!IsPointer(address))
    {
        Console.WriteLine($"{label}: <null>");
        return;
    }

    byte[] bytes;
    try
    {
        bytes = ReadMemory(process, address, length);
    }
    catch (Win32Exception exception)
    {
        Console.WriteLine($"{label}: 0x{address:X} <unreadable: {exception.Message}>");
        return;
    }

    Console.WriteLine();
    Console.WriteLine($"{label}: 0x{address:X} Class={ResolveObjectClassName(process, names, address, nameCache)}");
    DumpFloatMatches(label, address, bytes, 0, bytes.Length, floatToFind);
}

static void DumpInlineStructFloats(
    string label,
    ulong address,
    byte[] containerBytes,
    int offset,
    int length,
    float? floatToFind)
{
    Console.WriteLine();
    Console.WriteLine($"{label}: 0x{address:X}");
    DumpFloatMatches(label, address, containerBytes, offset, length, floatToFind);
}

static void DumpFloatMatches(
    string label,
    ulong address,
    byte[] bytes,
    int offset,
    int length,
    float? floatToFind)
{
    var printed = 0;
    for (var index = offset; index + 4 <= offset + length; index += 4)
    {
        var value = BitConverter.ToSingle(bytes, index);
        if (!IsInterestingFloat(value, floatToFind))
        {
            continue;
        }

        Console.WriteLine($"  +0x{index - offset:X4} / abs 0x{address + (ulong)(index - offset):X}: float={value:0.###}");
        printed++;
        if (printed >= 80)
        {
            Console.WriteLine("  ... truncated float output ...");
            break;
        }
    }

    if (printed == 0)
    {
        Console.WriteLine(floatToFind is null
            ? "  <no interesting floats>"
            : $"  <no floats near {floatToFind.Value:0.###}>");
    }
}

static bool IsInterestingFloat(float value, float? floatToFind)
{
    if (float.IsNaN(value) || float.IsInfinity(value))
    {
        return false;
    }

    if (floatToFind is { } target)
    {
        return Math.Abs(value - target) <= 0.01f;
    }

    return Math.Abs(value) is >= 0.01f and <= 1_000f;
}

static (ulong Address, string OuterName) FindPlayerInventoryComponent(
    SafeProcessHandle process,
    Candidate objectArray,
    FNamePoolReader names)
{
    byte[] chunkTable;
    try
    {
        chunkTable = ReadMemory(process, objectArray.Objects, checked(objectArray.NumChunks * 8));
    }
    catch (Win32Exception)
    {
        return default;
    }

    var chunks = new ulong[objectArray.NumChunks];
    for (var index = 0; index < chunks.Length; index++)
    {
        chunks[index] = ReadUInt64(chunkTable, index * 8);
    }

    var nameCache = new Dictionary<ulong, string>();
    (ulong Address, string OuterName) fallback = default;

    for (var objectIndex = 0; objectIndex < objectArray.NumElements; objectIndex++)
    {
        var chunk = chunks[objectIndex / 65_536];
        if (!IsPointer(chunk))
        {
            continue;
        }

        var itemAddress = chunk + (ulong)(objectIndex % 65_536) * 0x18;
        byte[] item;
        try
        {
            item = ReadMemory(process, itemAddress, 0x18);
        }
        catch (Win32Exception)
        {
            continue;
        }

        var objectAddress = ReadUInt64(item, 0);
        if (!IsPointer(objectAddress))
        {
            continue;
        }

        byte[] unrealObject;
        try
        {
            unrealObject = ReadMemory(process, objectAddress, 0x28);
        }
        catch (Win32Exception)
        {
            continue;
        }

        var className = ResolveObjectName(process, names, ReadUInt64(unrealObject, 0x10), nameCache);
        if (!className.Equals("InventoryComponent", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var objectName = FormatUObjectName(names.Resolve(ReadInt32(unrealObject, 0x18)), ReadInt32(unrealObject, 0x1C));
        var outerName = ResolveObjectName(process, names, ReadUInt64(unrealObject, 0x20), nameCache);
        if (objectName.StartsWith("Default__", StringComparison.OrdinalIgnoreCase) ||
            outerName.StartsWith("Default__", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (outerName.Contains("BP_SkyverseCharacter_C", StringComparison.OrdinalIgnoreCase))
        {
            return (objectAddress, outerName);
        }

        fallback = (objectAddress, outerName);
    }

    return fallback;
}

static int DetectInventorySlotPointerStride(
    SafeProcessHandle process,
    FNamePoolReader names,
    Dictionary<ulong, string> nameCache,
    ulong slotsData,
    int slotsNum)
{
    var bestStride = 8;
    var bestScore = -1;
    foreach (var stride in new[] { 8, 16, 24, 32 })
    {
        var score = 0;
        for (var index = 0; index < Math.Min(slotsNum, 24); index++)
        {
            try
            {
                var pointer = ReadUInt64(ReadMemory(process, slotsData + (ulong)(index * stride), 8), 0);
                if (IsPointer(pointer) && IsUObjectClass(process, names, nameCache, pointer, "InventorySlot"))
                {
                    score++;
                }
            }
            catch (Win32Exception)
            {
                // Ignore transient unreadable candidates while scoring.
            }
        }

        if (score > bestScore)
        {
            bestScore = score;
            bestStride = stride;
        }
    }

    return bestStride;
}

static bool IsUObjectClass(
    SafeProcessHandle process,
    FNamePoolReader names,
    Dictionary<ulong, string> nameCache,
    ulong objectAddress,
    string expectedClassName)
{
    var className = ResolveObjectClassName(process, names, objectAddress, nameCache);
    return className.Equals(expectedClassName, StringComparison.OrdinalIgnoreCase);
}

static string ResolveObjectClassName(
    SafeProcessHandle process,
    FNamePoolReader names,
    ulong objectAddress,
    Dictionary<ulong, string> cache)
{
    if (!IsPointer(objectAddress))
    {
        return "";
    }

    try
    {
        var objectBytes = ReadMemory(process, objectAddress, 0x18);
        return ResolveObjectName(process, names, ReadUInt64(objectBytes, 0x10), cache);
    }
    catch (Win32Exception)
    {
        return "";
    }
}

static int RunExternalTrainer(
    SafeProcessHandle process,
    Candidate objectArray,
    FNamePoolReader names,
    TrainerOptions options)
{
    var playerStats = FindPlayerStatisticsComponent(process, objectArray, names);
    if (playerStats.Address == 0)
    {
        Console.Error.WriteLine("Could not find the live player CharacterStatisticsComponent.");
        Console.Error.WriteLine("Load into a world as the player, then retry. No writes were made.");
        return 1;
    }

    Console.WriteLine($"Trainer attached to player stats: 0x{playerStats.Address:X} ({playerStats.OuterName})");
    (ulong Address, string OuterName) playerInventory = default;
    if (options.ItemAmount is not null)
    {
        playerInventory = FindPlayerInventoryComponent(process, objectArray, names);
        if (playerInventory.Address == 0)
        {
            Console.Error.WriteLine("Could not find the live player InventoryComponent. No writes were made.");
            return 1;
        }

        Console.WriteLine($"Trainer attached to player inventory: 0x{playerInventory.Address:X} ({playerInventory.OuterName})");
    }

    Console.WriteLine(options.Once
        ? "Applying selected features once."
        : $"Applying selected features every {options.IntervalMs} ms. Press Ctrl+C to stop.");

    var stop = false;
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        stop = true;
    };

    var tick = 0;
    do
    {
        tick++;
        try
        {
            if (!options.Once &&
                tick > 1 &&
                (tick % 20 == 1 || !IsPlayerStatisticsStillAttached(process, playerStats)))
            {
                var refreshed = FindPlayerStatisticsComponent(process, objectArray, names);
                if (refreshed.Address != 0)
                {
                    playerStats = refreshed;
                }
            }

            ApplyTrainerOptions(process, playerStats.Address, playerStats.ActorAddress, playerInventory.Address, options, tick);
        }
        catch (Win32Exception exception)
        {
            if (!options.Once)
            {
                var refreshed = FindPlayerStatisticsComponent(process, objectArray, names);
                if (refreshed.Address != 0)
                {
                    playerStats = refreshed;
                    try
                    {
                        ApplyTrainerOptions(process, playerStats.Address, playerStats.ActorAddress, playerInventory.Address, options, tick);
                        continue;
                    }
                    catch (Win32Exception refreshedException)
                    {
                        exception = refreshedException;
                    }
                }
            }

            Console.Error.WriteLine($"Trainer write failed: {exception.Message}");
            return 1;
        }

        if (options.Once)
        {
            break;
        }

        Thread.Sleep(options.IntervalMs);
    } while (!stop);

    Console.WriteLine("Trainer stopped.");
    return 0;
}

static bool IsPlayerStatisticsStillAttached(
    SafeProcessHandle process,
    (ulong Address, string OuterName, ulong ActorAddress) playerStats)
{
    if (!IsPointer(playerStats.Address) || !IsPointer(playerStats.ActorAddress))
    {
        return false;
    }

    try
    {
        var actorStatsAddress = ReadUInt64(ReadMemory(process, playerStats.ActorAddress + 0x0740, 8), 0);
        return actorStatsAddress == playerStats.Address;
    }
    catch (Win32Exception)
    {
        return false;
    }
}

static (ulong Address, string OuterName, ulong ActorAddress) FindPlayerStatisticsComponent(
    SafeProcessHandle process,
    Candidate objectArray,
    FNamePoolReader names)
{
    byte[] chunkTable;
    try
    {
        chunkTable = ReadMemory(process, objectArray.Objects, checked(objectArray.NumChunks * 8));
    }
    catch (Win32Exception)
    {
        return default;
    }

    var chunks = new ulong[objectArray.NumChunks];
    for (var index = 0; index < chunks.Length; index++)
    {
        chunks[index] = ReadUInt64(chunkTable, index * 8);
    }

    var nameCache = new Dictionary<ulong, string>();
    (ulong Address, string OuterName, ulong ActorAddress) fallback = default;

    for (var objectIndex = 0; objectIndex < objectArray.NumElements; objectIndex++)
    {
        var chunk = chunks[objectIndex / 65_536];
        if (!IsPointer(chunk))
        {
            continue;
        }

        var itemAddress = chunk + (ulong)(objectIndex % 65_536) * 0x18;
        byte[] item;
        try
        {
            item = ReadMemory(process, itemAddress, 0x18);
        }
        catch (Win32Exception)
        {
            continue;
        }

        var objectAddress = ReadUInt64(item, 0);
        if (!IsPointer(objectAddress))
        {
            continue;
        }

        byte[] unrealObject;
        try
        {
            unrealObject = ReadMemory(process, objectAddress, 0x28);
        }
        catch (Win32Exception)
        {
            continue;
        }

        var className = ResolveObjectName(process, names, ReadUInt64(unrealObject, 0x10), nameCache);
        if (!className.Equals("CharacterStatisticsComponent", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var objectName = FormatUObjectName(names.Resolve(ReadInt32(unrealObject, 0x18)), ReadInt32(unrealObject, 0x1C));
        var outerAddress = ReadUInt64(unrealObject, 0x20);
        var outerName = ResolveObjectName(process, names, outerAddress, nameCache);
        if (objectName.StartsWith("Default__", StringComparison.OrdinalIgnoreCase) ||
            outerName.StartsWith("Default__", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var hp = ReadStatisticPointer(process, objectAddress, 0x00F0);
        var stamina = ReadStatisticPointer(process, objectAddress, 0x00F8);
        if (!IsPointer(hp) || !IsPointer(stamina))
        {
            continue;
        }

        if (outerName.Contains("BP_SkyverseCharacter_C", StringComparison.OrdinalIgnoreCase))
        {
            return (objectAddress, outerName, outerAddress);
        }

        fallback = (objectAddress, outerName, outerAddress);
    }

    return fallback;
}

static void ApplyTrainerOptions(
    SafeProcessHandle process,
    ulong playerStats,
    ulong playerActor,
    ulong playerInventory,
    TrainerOptions options,
    int tick)
{
    if (options.ResetPlayerDefaults)
    {
        ResetKnownPlayerDefaults(process, playerStats);
        SetActorCanBeDamaged(process, playerActor, true);
    }

    if (options.InfiniteHealth)
    {
        EnableHealthGodMode(process, playerStats, playerActor);
    }
    else if (options.DisableInfiniteHealth)
    {
        SetRangedStatisticFreezeRule(process, playerStats, 0x00F0, 0);
        SetActorCanBeDamaged(process, playerActor, true);
    }

    if (options.InfiniteStamina)
    {
        DisableStaminaCosts(process, playerStats);
    }
    else if (options.DisableInfiniteStamina)
    {
        RestoreStaminaCosts(process, playerStats);
    }

    if (options.ExperienceMultiplier is { } xp)
    {
        SetStatisticAbsolute(process, playerStats, 0x0120, xp, writeBase: true);
    }
    else if (options.ResetExperienceMultiplier)
    {
        SetStatisticAbsolute(process, playerStats, 0x0120, 1.0f, writeBase: true);
    }

    if (options.JumpMultiplier is { } jump)
    {
        SetStatisticAbsolute(process, playerStats, 0x0170, 1.0f * jump, writeBase: true);
    }
    else if (options.ResetJumpMultiplier)
    {
        SetStatisticAbsolute(process, playerStats, 0x0170, 1.0f, writeBase: true);
    }

    if (options.SpeedMultiplier is { } speed)
    {
        SetSpeedMultiplierFromDefaults(process, playerStats, speed);
    }
    else if (options.ResetSpeedMultiplier)
    {
        ResetSpeedDefaults(process, playerStats);
    }

    if (options.DamageMultiplier is { } damage)
    {
        SetStatisticAbsolute(process, playerStats, 0x0230, damage, writeBase: true);
    }
    else if (options.ResetDamageMultiplier)
    {
        SetStatisticAbsolute(process, playerStats, 0x0230, 1.0f, writeBase: true);
    }

    if (options.BlockDamageMultiplier is { } blockDamage)
    {
        SetStatisticAbsolute(process, playerStats, 0x0240, blockDamage, writeBase: true);
    }
    else if (options.ResetBlockDamageMultiplier)
    {
        SetStatisticAbsolute(process, playerStats, 0x0240, 1.0f, writeBase: true);
    }

    if (options.NoDurabilityLoss)
    {
        SetStatisticAbsolute(process, playerStats, 0x0248, 0.0f, writeBase: true);
    }
    else if (options.DurabilityUsageMultiplier is { } durability)
    {
        SetStatisticAbsolute(process, playerStats, 0x0248, durability, writeBase: true);
    }
    else if (options.ResetDurabilityUsageMultiplier)
    {
        SetStatisticAbsolute(process, playerStats, 0x0248, 1.0f, writeBase: true);
    }

    if (options.ItemAmount is { } itemAmount && IsPointer(playerInventory))
    {
        PinPlayerInventorySlotOneAmount(process, playerInventory, itemAmount);
    }

    if (tick == 1 || tick % 20 == 0)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Trainer tick {tick}");
    }
}

static void PinPlayerInventorySlotOneAmount(SafeProcessHandle process, ulong inventoryAddress, int targetAmount)
{
    var inventoryBytes = ReadMemory(process, inventoryAddress, 0x198);
    var slotsData = ReadUInt64(inventoryBytes, 0x188);
    var slotsNum = ReadInt32(inventoryBytes, 0x190);
    if (!IsPointer(slotsData) || slotsNum <= 0 || slotsNum > 10_000)
    {
        return;
    }

    var slotAddress = ReadUInt64(ReadMemory(process, slotsData, 8), 0);
    if (!IsPointer(slotAddress))
    {
        return;
    }

    byte[] slotBytes;
    try
    {
        slotBytes = ReadMemory(process, slotAddress, 0x58);
    }
    catch (Win32Exception)
    {
        return;
    }

    var itemAddress = ReadUInt64(slotBytes, 0x48);
    var amount = ReadInt32(slotBytes, 0x50);
    if (IsPointer(itemAddress) && amount > 0 && amount < targetAmount)
    {
        WriteInt32(process, slotAddress + 0x50, targetAmount);
    }
}

static void ResetKnownPlayerDefaults(SafeProcessHandle process, ulong playerStats)
{
    SetRangedStatisticFreezeRule(process, playerStats, 0x00F0, 0); // Hp FreezeRule
    SetRangedStatisticFreezeRule(process, playerStats, 0x00F8, 0); // Stamina FreezeRule
    SetStatisticCurrentToBase(process, playerStats, 0x00F0, "Hp");
    SetStatisticCurrentToBase(process, playerStats, 0x00F8, "Stamina");
    SetStatisticAbsolute(process, playerStats, 0x0120, 1.0f, writeBase: true); // XPGain
    SetStatisticAbsolute(process, playerStats, 0x0168, 0.0f, writeBase: true); // JumpStaminaCost
    SetStatisticAbsolute(process, playerStats, 0x0170, 1.0f, writeBase: true); // JumpBoost
    SetStatisticAbsolute(process, playerStats, 0x0180, 3.0f, writeBase: true); // SprintStaminaCostRate
    SetStatisticAbsolute(process, playerStats, 0x0188, 100.0f, writeBase: true); // ToolUseStaminaCostRate
    SetStatisticAbsolute(process, playerStats, 0x0190, 2.0f, writeBase: true); // SwimmingSprintStaminaCostRate
    SetStatisticAbsolute(process, playerStats, 0x0198, 350.0f, writeBase: true); // WalkMaxSpeed
    SetStatisticAbsolute(process, playerStats, 0x01A0, 500.0f, writeBase: true); // SprintMaxSpeed
    SetStatisticAbsolute(process, playerStats, 0x01A8, 180.0f, writeBase: true); // CrouchMaxSpeed
    SetStatisticAbsolute(process, playerStats, 0x01B0, 150.0f, writeBase: true); // SprintSwimmingMaxSpeed
    SetStatisticAbsolute(process, playerStats, 0x01B8, 100.0f, writeBase: true); // SwimmingMaxSpeed
    SetStatisticAbsolute(process, playerStats, 0x0230, 1.0f, writeBase: true); // DmgMultiplier
    SetStatisticAbsolute(process, playerStats, 0x0240, 1.0f, writeBase: true); // BlockDmgMultiplier
    SetStatisticAbsolute(process, playerStats, 0x0248, 1.0f, writeBase: true); // DurablilityUsageMultiplier
}

static void DisableStaminaCosts(SafeProcessHandle process, ulong playerStats)
{
    SetStatisticAbsolute(process, playerStats, 0x0168, 0.0f, writeBase: true); // JumpStaminaCost
    SetStatisticAbsolute(process, playerStats, 0x0180, 0.0f, writeBase: true); // SprintStaminaCostRate
    SetStatisticAbsolute(process, playerStats, 0x0188, 0.0f, writeBase: true); // ToolUseStaminaCostRate
    SetStatisticAbsolute(process, playerStats, 0x0190, 0.0f, writeBase: true); // SwimmingSprintStaminaCostRate
}

static void RestoreStaminaCosts(SafeProcessHandle process, ulong playerStats)
{
    SetStatisticAbsolute(process, playerStats, 0x0168, 0.0f, writeBase: true); // JumpStaminaCost
    SetStatisticAbsolute(process, playerStats, 0x0180, 3.0f, writeBase: true); // SprintStaminaCostRate
    SetStatisticAbsolute(process, playerStats, 0x0188, 100.0f, writeBase: true); // ToolUseStaminaCostRate
    SetStatisticAbsolute(process, playerStats, 0x0190, 2.0f, writeBase: true); // SwimmingSprintStaminaCostRate
}

static void ResetSpeedDefaults(SafeProcessHandle process, ulong playerStats)
{
    SetStatisticAbsolute(process, playerStats, 0x0198, 350.0f, writeBase: true); // WalkMaxSpeed
    SetStatisticAbsolute(process, playerStats, 0x01A0, 500.0f, writeBase: true); // SprintMaxSpeed
    SetStatisticAbsolute(process, playerStats, 0x01A8, 180.0f, writeBase: true); // CrouchMaxSpeed
    SetStatisticAbsolute(process, playerStats, 0x01B0, 150.0f, writeBase: true); // SprintSwimmingMaxSpeed
    SetStatisticAbsolute(process, playerStats, 0x01B8, 100.0f, writeBase: true); // SwimmingMaxSpeed
}

static void SetSpeedMultiplierFromDefaults(SafeProcessHandle process, ulong playerStats, float multiplier)
{
    SetStatisticAbsolute(process, playerStats, 0x0198, 350.0f * multiplier, writeBase: true); // WalkMaxSpeed
    SetStatisticAbsolute(process, playerStats, 0x01A0, 500.0f * multiplier, writeBase: true); // SprintMaxSpeed
    SetStatisticAbsolute(process, playerStats, 0x01A8, 180.0f * multiplier, writeBase: true); // CrouchMaxSpeed
    SetStatisticAbsolute(process, playerStats, 0x01B0, 150.0f * multiplier, writeBase: true); // SprintSwimmingMaxSpeed
    SetStatisticAbsolute(process, playerStats, 0x01B8, 100.0f * multiplier, writeBase: true); // SwimmingMaxSpeed
}

static ulong ReadStatisticPointer(SafeProcessHandle process, ulong componentAddress, int offset)
{
    var bytes = ReadMemory(process, componentAddress + (uint)offset, 8);
    return ReadUInt64(bytes, 0);
}

static StatisticSnapshot ReadStatistic(SafeProcessHandle process, ulong statisticAddress)
{
    var bytes = ReadMemory(process, statisticAddress, 0xB8);
    return new StatisticSnapshot(
        BitConverter.ToSingle(bytes, 0x28),
        BitConverter.ToSingle(bytes, 0x2C),
        BitConverter.ToSingle(bytes, 0xB4));
}

static void EnableHealthGodMode(SafeProcessHandle process, ulong componentAddress, ulong actorAddress)
{
    SetActorCanBeDamaged(process, actorAddress, false);

    var statisticAddress = ReadStatisticPointer(process, componentAddress, 0x00F0);
    if (!IsPointer(statisticAddress))
    {
        return;
    }

    var snapshot = ReadRangedStatistic(process, statisticAddress);
    var fullValue = Math.Max(snapshot.Current, snapshot.BaseValue);
    if (fullValue > 0 && Math.Abs(snapshot.RangedCurrent - fullValue) > 0.001f)
    {
        // Top HP off when enabling or recovering from a cleared freeze rule,
        // then let the game's own RangedStatistic freeze rule prevent damage
        // from moving the live HP bar.
        WriteFloat(process, statisticAddress + 0xB4, fullValue);
    }

    if (fullValue > 0 && (Math.Abs(snapshot.Current - fullValue) > 0.001f || Math.Abs(snapshot.BaseValue - fullValue) > 0.001f))
    {
        WriteFloat(process, statisticAddress + 0x28, fullValue);
        WriteFloat(process, statisticAddress + 0x2C, fullValue);
    }

    const byte statFreezeBoth = 3; // EStatFreeze::BothFreeze
    if (snapshot.FreezeRule != statFreezeBoth)
    {
        SetRangedStatisticFreezeRule(process, componentAddress, 0x00F0, statFreezeBoth);
    }
}

static void SetActorCanBeDamaged(SafeProcessHandle process, ulong actorAddress, bool canBeDamaged)
{
    if (!IsPointer(actorAddress))
    {
        return;
    }

    const int actorFlagsOffset = 0x005A;
    const byte canBeDamagedMask = 0x04;

    var current = ReadMemory(process, actorAddress + actorFlagsOffset, 1)[0];
    var updated = canBeDamaged
        ? (byte)(current | canBeDamagedMask)
        : (byte)(current & ~canBeDamagedMask);

    if (updated != current)
    {
        WriteByte(process, actorAddress + actorFlagsOffset, updated);
    }
}

static RangedStatisticSnapshot ReadRangedStatistic(SafeProcessHandle process, ulong statisticAddress)
{
    var bytes = ReadMemory(process, statisticAddress, 0xEA);
    return new RangedStatisticSnapshot(
        BitConverter.ToSingle(bytes, 0x28),
        BitConverter.ToSingle(bytes, 0x2C),
        BitConverter.ToSingle(bytes, 0xB4),
        bytes[0xE9]);
}

static void SetStatisticCurrentToBase(SafeProcessHandle process, ulong componentAddress, int statPointerOffset, string label)
{
    var statisticAddress = ReadStatisticPointer(process, componentAddress, statPointerOffset);
    if (!IsPointer(statisticAddress))
    {
        return;
    }

    var snapshot = ReadStatistic(process, statisticAddress);
    if (snapshot.BaseValue > 0)
    {
        // RangedStatistic uses CurrentStat (+0xB4) as the live HP/stamina bar value.
        // CurrentValue/BaseValue (+0x28/+0x2C) are the stat/max values and do not
        // change when the player takes damage or spends stamina.
        WriteFloat(process, statisticAddress + 0xB4, snapshot.Current);
        WriteFloat(process, statisticAddress + 0x28, snapshot.BaseValue);
    }
}

static void SetRangedStatisticFreezeRule(SafeProcessHandle process, ulong componentAddress, int statPointerOffset, byte value)
{
    var statisticAddress = ReadStatisticPointer(process, componentAddress, statPointerOffset);
    if (!IsPointer(statisticAddress))
    {
        return;
    }

    WriteByte(process, statisticAddress + 0xE9, value);
}

static void SetStatisticAbsolute(
    SafeProcessHandle process,
    ulong componentAddress,
    int statPointerOffset,
    float value,
    bool writeBase)
{
    var statisticAddress = ReadStatisticPointer(process, componentAddress, statPointerOffset);
    if (!IsPointer(statisticAddress))
    {
        return;
    }

    WriteFloat(process, statisticAddress + 0x28, value);
    if (writeBase)
    {
        WriteFloat(process, statisticAddress + 0x2C, value);
    }
}

static void WriteFloat(SafeProcessHandle process, ulong address, float value)
{
    var bytes = BitConverter.GetBytes(value);
    if (!NativeMethods.WriteProcessMemory(
            process,
            (nint)address,
            bytes,
            (nuint)bytes.Length,
            out var written) ||
        written != (nuint)bytes.Length)
    {
        throw new Win32Exception(Marshal.GetLastWin32Error(), $"WriteProcessMemory failed at 0x{address:X}.");
    }
}

static void WriteInt32(SafeProcessHandle process, ulong address, int value)
{
    var bytes = BitConverter.GetBytes(value);
    if (!NativeMethods.WriteProcessMemory(
            process,
            (nint)address,
            bytes,
            (nuint)bytes.Length,
            out var written) ||
        written != (nuint)bytes.Length)
    {
        throw new Win32Exception(Marshal.GetLastWin32Error(), $"WriteProcessMemory failed at 0x{address:X}.");
    }
}

static void WriteByte(SafeProcessHandle process, ulong address, byte value)
{
    var bytes = new[] { value };
    if (!NativeMethods.WriteProcessMemory(
            process,
            (nint)address,
            bytes,
            (nuint)bytes.Length,
            out var written) ||
        written != (nuint)bytes.Length)
    {
        throw new Win32Exception(Marshal.GetLastWin32Error(), $"WriteProcessMemory failed at 0x{address:X}.");
    }
}

static List<ReflectedProperty> ReadStructProperties(
    SafeProcessHandle process,
    FNamePoolReader names,
    ulong structAddress,
    Dictionary<ulong, string> objectNameCache,
    bool includeSuper)
{
    var properties = new List<ReflectedProperty>();
    var current = structAddress;
    var seenStructs = new HashSet<ulong>();

    for (var depth = 0; IsPointer(current) && depth < 16 && seenStructs.Add(current); depth++)
    {
        byte[] structBytes;
        try
        {
            structBytes = ReadMemory(process, current, 0x60);
        }
        catch (Win32Exception)
        {
            break;
        }

        var owner = ResolveObjectName(process, names, current, objectNameCache);
        var super = ReadUInt64(structBytes, 0x40);
        var childProperties = ReadUInt64(structBytes, 0x50);
        var field = childProperties;
        var seenFields = new HashSet<ulong>();

        for (var fieldIndex = 0; IsPointer(field) && fieldIndex < 1_024 && seenFields.Add(field); fieldIndex++)
        {
            byte[] fieldBytes;
            try
            {
                fieldBytes = ReadMemory(process, field, 0x78);
            }
            catch (Win32Exception)
            {
                break;
            }

            var next = ReadUInt64(fieldBytes, 0x18);
            var propertyName = FormatUObjectName(names.Resolve(ReadInt32(fieldBytes, 0x20)), ReadInt32(fieldBytes, 0x24));
            var arrayDim = ReadInt32(fieldBytes, 0x30);
            var elementSize = ReadInt32(fieldBytes, 0x34);
            var offset = ReadInt32(fieldBytes, 0x44);
            var boolFieldSize = fieldBytes[0x70];
            var boolByteOffset = fieldBytes[0x71];
            var boolByteMask = fieldBytes[0x72];
            var boolFieldMask = fieldBytes[0x73];
            if (!string.IsNullOrWhiteSpace(propertyName) &&
                offset >= 0 &&
                offset < 0x10000 &&
                elementSize >= 0 &&
                elementSize < 0x10000)
            {
                properties.Add(new ReflectedProperty(
                    owner,
                    propertyName,
                    offset,
                    arrayDim,
                    elementSize,
                    boolFieldSize,
                    boolByteOffset,
                    boolByteMask,
                    boolFieldMask));
            }

            field = next;
        }

        if (!includeSuper)
        {
            break;
        }

        current = super;
    }

    return properties
        .GroupBy(property => (property.Owner, property.Name, property.Offset))
        .Select(group => group.First())
        .OrderBy(property => property.Offset)
        .ThenBy(property => property.Owner, StringComparer.OrdinalIgnoreCase)
        .ThenBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static string ResolveObjectName(
    SafeProcessHandle process,
    FNamePoolReader names,
    ulong objectAddress,
    Dictionary<ulong, string> cache)
{
    if (!IsPointer(objectAddress))
    {
        return "";
    }

    if (cache.TryGetValue(objectAddress, out var cached))
    {
        return cached;
    }

    try
    {
        var objectBytes = ReadMemory(process, objectAddress, 0x20);
        var nameIndex = ReadInt32(objectBytes, 0x18);
        var nameNumber = ReadInt32(objectBytes, 0x1C);
        var name = FormatUObjectName(names.Resolve(nameIndex), nameNumber);
        cache[objectAddress] = name;
        return name;
    }
    catch (Win32Exception)
    {
        cache[objectAddress] = "";
        return "";
    }
}

static string FormatUObjectName(string name, int number)
{
    if (string.IsNullOrWhiteSpace(name))
    {
        return "";
    }

    return number > 0 ? $"{name}_{number - 1}" : name;
}

static bool MatchesAny(string value, IEnumerable<string> keywords) =>
    !string.IsNullOrEmpty(value) &&
    keywords.Any(keyword => value.Contains(keyword, StringComparison.OrdinalIgnoreCase));

static byte[] ReadMemory(SafeProcessHandle process, ulong address, int length)
{
    var bytes = new byte[length];
    if (!NativeMethods.ReadProcessMemory(
            process,
            (nint)address,
            bytes,
            (nuint)bytes.Length,
            out var read) ||
        read != (nuint)bytes.Length)
    {
        throw new Win32Exception(Marshal.GetLastWin32Error(), $"ReadProcessMemory failed at 0x{address:X}.");
    }

    return bytes;
}

internal sealed record Candidate(
    ulong Address,
    uint Rva,
    ulong Objects,
    int MaxElements,
    int NumElements,
    int MaxChunks,
    int NumChunks,
    int CheckedObjects,
    int MatchingIndices);

internal sealed record NamePoolCandidate(
    ulong Address,
    uint Rva,
    int CurrentBlock,
    int CurrentByteCursor,
    string[] FirstNames);

internal sealed record NameBlockCandidate(ulong Address, string[] FirstNames);

internal sealed record ReflectedProperty(
    string Owner,
    string Name,
    int Offset,
    int ArrayDim,
    int ElementSize,
    byte BoolFieldSize,
    byte BoolByteOffset,
    byte BoolByteMask,
    byte BoolFieldMask);

internal sealed class TrainerOptions
{
    internal bool IsValid { get; set; } = true;
    internal bool Enabled { get; set; }
    internal bool InfiniteHealth { get; set; }
    internal bool InfiniteStamina { get; set; }
    internal bool NoDurabilityLoss { get; set; }
    internal bool DisableInfiniteHealth { get; set; }
    internal bool DisableInfiniteStamina { get; set; }
    internal bool ResetDamageMultiplier { get; set; }
    internal bool ResetBlockDamageMultiplier { get; set; }
    internal bool ResetExperienceMultiplier { get; set; }
    internal bool ResetSpeedMultiplier { get; set; }
    internal bool ResetJumpMultiplier { get; set; }
    internal bool ResetDurabilityUsageMultiplier { get; set; }
    internal bool ResetPlayerDefaults { get; set; }
    internal bool Once { get; set; }
    internal float? DamageMultiplier { get; set; }
    internal float? BlockDamageMultiplier { get; set; }
    internal float? ExperienceMultiplier { get; set; }
    internal float? SpeedMultiplier { get; set; }
    internal float? JumpMultiplier { get; set; }
    internal float? DurabilityUsageMultiplier { get; set; }
    internal int? ItemAmount { get; set; }
    internal int IntervalMs { get; set; } = 500;

    internal bool HasWriteFlags =>
        InfiniteHealth ||
        InfiniteStamina ||
        NoDurabilityLoss ||
        DisableInfiniteHealth ||
        DisableInfiniteStamina ||
        ResetDamageMultiplier ||
        ResetBlockDamageMultiplier ||
        ResetExperienceMultiplier ||
        ResetSpeedMultiplier ||
        ResetJumpMultiplier ||
        ResetDurabilityUsageMultiplier ||
        ResetPlayerDefaults ||
        DamageMultiplier is not null ||
        BlockDamageMultiplier is not null ||
        ExperienceMultiplier is not null ||
        SpeedMultiplier is not null ||
        JumpMultiplier is not null ||
        DurabilityUsageMultiplier is not null ||
        ItemAmount is not null;
}

internal sealed record StatisticSnapshot(float Current, float BaseValue, float RangedCurrent);

internal sealed record RangedStatisticSnapshot(float Current, float BaseValue, float RangedCurrent, byte FreezeRule);

internal sealed class FNamePoolReader(SafeProcessHandle process, ulong address)
{
    private readonly Dictionary<int, string> _nameCache = new();
    private readonly Dictionary<int, ulong> _blockCache = new();

    public string Resolve(int comparisonIndex)
    {
        if (comparisonIndex < 0)
        {
            return "";
        }

        if (_nameCache.TryGetValue(comparisonIndex, out var cached))
        {
            return cached;
        }

        var block = comparisonIndex >> 16;
        var offset = comparisonIndex & 0xFFFF;
        var name = TryResolve(block, offset * 2) ?? TryResolve(block, offset) ?? "";
        _nameCache[comparisonIndex] = name;
        return name;
    }

    private string? TryResolve(int block, int byteOffset)
    {
        if (block < 0 || byteOffset < 0)
        {
            return null;
        }

        var blockAddress = GetBlockAddress(block);
        if (!IsLikelyPointer(blockAddress))
        {
            return null;
        }

        try
        {
            var bytes = ReadMemoryLocal(process, blockAddress + (uint)byteOffset, 2 + 2_048);
            return TryDecodeEntry(bytes, out var name) ? name : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private ulong GetBlockAddress(int block)
    {
        if (_blockCache.TryGetValue(block, out var cached))
        {
            return cached;
        }

        try
        {
            var bytes = ReadMemoryLocal(process, address + 0x10 + (ulong)block * 8, 8);
            var blockAddress = BitConverter.ToUInt64(bytes, 0);
            _blockCache[block] = blockAddress;
            return blockAddress;
        }
        catch (Win32Exception)
        {
            _blockCache[block] = 0;
            return 0;
        }
    }

    private static bool IsLikelyPointer(ulong value) => value >= 0x1_0000 && value < 0x0000_8000_0000_0000;

    private static byte[] ReadMemoryLocal(SafeProcessHandle process, ulong readAddress, int length)
    {
        var bytes = new byte[length];
        if (!NativeMethods.ReadProcessMemory(
                process,
                (nint)readAddress,
                bytes,
                (nuint)bytes.Length,
                out var read) ||
            read != (nuint)bytes.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"ReadProcessMemory failed at 0x{readAddress:X}.");
        }

        return bytes;
    }

    private static bool TryDecodeEntry(byte[] bytes, out string name)
    {
        name = "";
        if (bytes.Length < 2)
        {
            return false;
        }

        var header = BitConverter.ToUInt16(bytes, 0);
        return TryDecodeEntryWithLength(bytes, (header & 1) != 0, header >> 1, out name) ||
               TryDecodeEntryWithLength(bytes, (header & 1) != 0, header >> 6, out name);
    }

    private static bool TryDecodeEntryWithLength(byte[] bytes, bool isWide, int length, out string name)
    {
        name = "";
        if (length <= 0 || length > 1_024)
        {
            return false;
        }

        var byteLength = isWide ? checked(length * 2) : length;
        if (2 + byteLength > bytes.Length)
        {
            return false;
        }

        name = isWide
            ? Encoding.Unicode.GetString(bytes, 2, byteLength)
            : Encoding.ASCII.GetString(bytes, 2, byteLength);
        return name.Length > 0 && name.All(character => !char.IsControl(character) && character != '\uFFFD');
    }
}

internal static class NativeMethods
{
    internal const uint ProcessVmRead = 0x0010;
    internal const uint ProcessVmWrite = 0x0020;
    internal const uint ProcessVmOperation = 0x0008;
    internal const uint ProcessQueryInformation = 0x0400;
    internal const uint MemCommit = 0x1000;
    internal const uint PageNoAccess = 0x01;
    internal const uint PageReadonly = 0x02;
    internal const uint PageReadwrite = 0x04;
    internal const uint PageWritecopy = 0x08;
    internal const uint PageExecuteRead = 0x20;
    internal const uint PageExecuteReadwrite = 0x40;
    internal const uint PageExecuteWritecopy = 0x80;
    internal const uint PageGuard = 0x100;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadProcessMemory(
        SafeProcessHandle process,
        nint baseAddress,
        byte[] buffer,
        nuint size,
        out nuint numberOfBytesRead);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WriteProcessMemory(
        SafeProcessHandle process,
        nint baseAddress,
        byte[] buffer,
        nuint size,
        out nuint numberOfBytesWritten);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nuint VirtualQueryEx(
        SafeProcessHandle process,
        nint address,
        out MemoryBasicInformation buffer,
        nuint length);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryBasicInformation
    {
        internal nuint BaseAddress;
        internal nuint AllocationBase;
        internal uint AllocationProtect;
        internal ushort PartitionId;
        internal nuint RegionSize;
        internal uint State;
        internal uint Protect;
        internal uint Type;
    }
}

internal sealed class SafeProcessHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeProcessHandle() : base(true)
    {
    }

    protected override bool ReleaseHandle() => CloseHandle(handle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
