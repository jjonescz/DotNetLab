namespace DotNetLab;

public interface IJitAsmDisassembler
{
    string Disassemble(MemoryStream emitStream, ImmutableArray<RefAssembly> references);
}
