-- Everwind-Win64-Shipping.exe
-- SHA-256: 6BE4DBB5E4F93C8F73AEBE90BB7613E0A9186DCB16045B3423FDC605DB4C06B5
--
-- The signature starts in UE 5.5 garbage-collection code and reaches a CMP
-- against FUObjectArray::ObjFirstGCIndex. RIP-relative bytes are wildcarded so
-- ASLR and ordinary link-address changes do not affect the match.

function Register()
    return "44 8B 05 ?? ?? ?? ?? 45 85 C0 0F 8E ?? ?? ?? ?? " ..
           "8B 3D ?? ?? ?? ?? FF C7 89 3D ?? ?? ?? ?? 44 39 C7 7C 0D " ..
           "83 3D ?? ?? ?? ?? 00 0F 89"
end

function OnMatchFound(matchAddress)
    local cmpInstruction = matchAddress + 0x23
    local nextInstruction = cmpInstruction + 0x07
    local displacement = DerefToInt32(cmpInstruction + 0x02)
    return nextInstruction + displacement
end

