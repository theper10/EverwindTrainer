using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;
using Iced.Intel;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: Everwind.Analyzer <exe> <ASCII text|rva:0x1234> [context instructions]");
    return 2;
}

var path = Path.GetFullPath(args[0]);
var targetArgument = args[1];
var isRva = targetArgument.StartsWith("rva:", StringComparison.OrdinalIgnoreCase);
var needle = isRva ? null : Encoding.ASCII.GetBytes(targetArgument);
var context = args.Length >= 3 && int.TryParse(args[2], out var parsed) ? parsed : 24;

await using var stream = File.OpenRead(path);
using var pe = new PEReader(stream);
var headers = pe.PEHeaders;
var imageBase = headers.PEHeader?.ImageBase ?? throw new InvalidDataException("PE image base is unavailable.");
var sections = headers.SectionHeaders;

var targets = new List<(string Section, int FileOffset, ulong Va)>();
if (isRva)
{
    var rvaText = targetArgument[4..];
    if (rvaText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
    {
        rvaText = rvaText[2..];
    }

    if (!uint.TryParse(rvaText, System.Globalization.NumberStyles.HexNumber, null, out var rva))
    {
        throw new ArgumentException($"Invalid RVA '{targetArgument}'. Use rva:0x1234.");
    }

    targets.Add(("RVA", -1, imageBase + rva));
    Console.WriteLine($"Target RVA=0x{rva:X}, VA=0x{imageBase + rva:X}");
}
else
{
    foreach (var section in sections)
    {
        stream.Position = section.PointerToRawData;
        var bytes = new byte[section.SizeOfRawData];
        await stream.ReadExactlyAsync(bytes);

        foreach (var offset in FindAll(bytes, needle!))
        {
            var fileOffset = section.PointerToRawData + offset;
            var va = imageBase + (uint)section.VirtualAddress + (uint)offset;
            targets.Add((section.Name, fileOffset, va));
            Console.WriteLine($"String in {section.Name}: file=0x{fileOffset:X}, VA=0x{va:X}");
        }
    }
}

if (targets.Count == 0)
{
    Console.Error.WriteLine("Target was not found.");
    return 1;
}

var textSection = sections.FirstOrDefault(section => section.Name == ".text");
if (textSection.Name != ".text")
{
    throw new InvalidDataException("The PE has no .text section.");
}

stream.Position = textSection.PointerToRawData;
var text = new byte[textSection.SizeOfRawData];
await stream.ReadExactlyAsync(text);

var decoder = Iced.Intel.Decoder.Create(64, new ByteArrayCodeReader(text));
decoder.IP = imageBase + (uint)textSection.VirtualAddress;
var textEnd = decoder.IP + (uint)text.Length;
var formatter = new IntelFormatter();
formatter.Options.RipRelativeAddresses = true;
formatter.Options.DigitSeparator = "`";
var output = new StringOutput();
var recent = new Queue<Instruction>();
var pending = new List<(ulong Target, int Remaining)>();

while (decoder.IP < textEnd)
{
    decoder.Decode(out var instruction);

    for (var index = pending.Count - 1; index >= 0; index--)
    {
        var item = pending[index];
        PrintInstruction(instruction, formatter, output, "    ");
        item.Remaining--;
        if (item.Remaining <= 0)
        {
            Console.WriteLine();
            pending.RemoveAt(index);
        }
        else
        {
            pending[index] = item;
        }
    }

    if (instruction.IsIPRelativeMemoryOperand &&
        targets.Any(target => target.Va == instruction.IPRelativeMemoryAddress))
    {
        Console.WriteLine($"\nXREF at 0x{instruction.IP:X} -> 0x{instruction.IPRelativeMemoryAddress:X}");
        foreach (var previous in recent)
        {
            PrintInstruction(previous, formatter, output, "    ");
        }
        PrintInstruction(instruction, formatter, output, " => ");
        pending.Add((instruction.IPRelativeMemoryAddress, context));
    }

    recent.Enqueue(instruction);
    while (recent.Count > context)
    {
        recent.Dequeue();
    }
}

return 0;

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

static void PrintInstruction(
    Instruction instruction,
    Formatter formatter,
    StringOutput output,
    string marker)
{
    output.Reset();
    formatter.Format(instruction, output);
    Console.WriteLine($"{marker}{instruction.IP:X16}  {output}");
}

sealed class StringOutput : FormatterOutput
{
    private readonly StringBuilder _builder = new();

    public override void Write(string text, FormatterTextKind kind) => _builder.Append(text);

    public void Reset() => _builder.Clear();

    public override string ToString() => _builder.ToString();
}
