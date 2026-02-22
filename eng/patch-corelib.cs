#!/usr/bin/env dotnet

// Used by `PatchCoreLib.targets`.

using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

if (args.Length != 2)
{
    Console.WriteLine("Usage: patch-corelib.cs <input CoreLib.dll> <output CoreLib.dll>");
    return 1;
}

var inputPath = Path.GetFullPath(args[0]);
var outputPath = Path.GetFullPath(args[1]);

if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"Input file not found: {inputPath}");
    return 1;
}

var bytes = File.ReadAllBytes(inputPath);

using (var peStream = new MemoryStream(bytes, writable: false))
using (var peReader = new PEReader(peStream))
{
    if (!peReader.HasMetadata)
    {
        Console.Error.WriteLine("Input is not a managed assembly.");
        return 1;
    }

    var reader = peReader.GetMetadataReader();

    var volatileType = FindTypeDefinition(reader, "System.Threading", "Volatile");
    var interlockedType = FindTypeDefinition(reader, "System.Threading", "Interlocked");
    var threadType = FindTypeDefinition(reader, "System.Threading", "Thread");

    var readBarrier = FindMethodDefinition(reader, volatileType, "ReadBarrier", 0);
    var writeBarrier = FindMethodDefinition(reader, volatileType, "WriteBarrier", 0);
    var memoryBarrier = FindMethodDefinition(reader, interlockedType, "MemoryBarrier", 0);

    var throwIfSingleThreaded = FindMethodDefinition(reader, threadType, "ThrowIfSingleThreaded", 0);

    // Workaround for https://github.com/jjonescz/DotNetLab/issues/129.
    PatchMethodBody(bytes, peReader, readBarrier, memoryBarrier, "Volatile.ReadBarrier");
    PatchMethodBody(bytes, peReader, writeBarrier, memoryBarrier, "Volatile.WriteBarrier");

    // Workaround for https://github.com/dotnet/roslyn/issues/82361.
    PatchMethodBodyToRet(bytes, peReader, throwIfSingleThreaded, "System.Threading.Thread.ThrowIfSingleThreaded");
}

Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
File.WriteAllBytes(outputPath, bytes);

Console.WriteLine("Patched CoreLib.dll.");
return 0;

static TypeDefinitionHandle FindTypeDefinition(MetadataReader reader, string @namespace, string name)
{
    foreach (var handle in reader.TypeDefinitions)
    {
        var typeDef = reader.GetTypeDefinition(handle);
        if (reader.StringComparer.Equals(typeDef.Namespace, @namespace) &&
            reader.StringComparer.Equals(typeDef.Name, name))
        {
            return handle;
        }
    }

    throw new InvalidOperationException($"Type not found: {@namespace}.{name}");
}

static MethodDefinitionHandle FindMethodDefinition(
    MetadataReader reader,
    TypeDefinitionHandle typeHandle,
    string name,
    int parameterCount)
{
    var typeDef = reader.GetTypeDefinition(typeHandle);

    foreach (var handle in typeDef.GetMethods())
    {
        var methodDef = reader.GetMethodDefinition(handle);
        if (!reader.StringComparer.Equals(methodDef.Name, name))
        {
            continue;
        }

        var parameters = methodDef.GetParameters();
        var count = 0;
        foreach (var parameterHandle in parameters)
        {
            var parameter = reader.GetParameter(parameterHandle);
            if (parameter.SequenceNumber != 0)
            {
                count++;
            }
        }

        if (count == parameterCount)
        {
            return handle;
        }
    }

    throw new InvalidOperationException($"Method not found: {name} with {parameterCount} parameters");
}

static void PatchMethodBody(
    byte[] peBytes,
    PEReader peReader,
    MethodDefinitionHandle targetMethod,
    MethodDefinitionHandle memoryBarrier,
    string displayName)
{
    var reader = peReader.GetMetadataReader();
    var methodDef = reader.GetMethodDefinition(targetMethod);

    if (methodDef.RelativeVirtualAddress == 0)
    {
        throw new InvalidOperationException($"Method has no body: {displayName}");
    }

    var bodyOffset = GetOffset(peReader, methodDef.RelativeVirtualAddress);

    // Determine method header format to locate IL bytes without changing header size.
    var headerByte = peBytes[bodyOffset];
    int headerSize;
    int codeSize;

    if ((headerByte & 0x3) == 0x2)
    {
        headerSize = 1;
        codeSize = headerByte >> 2;
    }
    else if ((headerByte & 0x3) == 0x3)
    {
        var flags = BitConverter.ToUInt16(peBytes, bodyOffset);
        headerSize = ((flags >> 12) & 0xF) * 4;
        codeSize = BitConverter.ToInt32(peBytes, bodyOffset + 4);
    }
    else
    {
        throw new InvalidOperationException($"Unknown method header format: {displayName}");
    }

    if (codeSize < 6)
    {
        throw new InvalidOperationException($"Method body too small to patch: {displayName}");
    }

    var ilStart = bodyOffset + headerSize;
    var callToken = MetadataTokens.GetToken(memoryBarrier);

    peBytes[ilStart + 0] = 0x28; // call
    var tokenBytes = BitConverter.GetBytes(callToken);
    Buffer.BlockCopy(tokenBytes, 0, peBytes, ilStart + 1, 4);
    peBytes[ilStart + 5] = 0x2A; // ret

    for (var i = 6; i < codeSize; i++)
    {
        peBytes[ilStart + i] = 0x00; // nop padding
    }
}

static void PatchMethodBodyToRet(
    byte[] peBytes,
    PEReader peReader,
    MethodDefinitionHandle targetMethod,
    string displayName)
{
    var reader = peReader.GetMetadataReader();
    var methodDef = reader.GetMethodDefinition(targetMethod);

    if (methodDef.RelativeVirtualAddress == 0)
    {
        throw new InvalidOperationException($"Method has no body: {displayName}");
    }

    var bodyOffset = GetOffset(peReader, methodDef.RelativeVirtualAddress);

    // Determine method header format to locate IL bytes without changing header size.
    var headerByte = peBytes[bodyOffset];
    int headerSize;
    int codeSize;

    if ((headerByte & 0x3) == 0x2)
    {
        headerSize = 1;
        codeSize = headerByte >> 2;
    }
    else if ((headerByte & 0x3) == 0x3)
    {
        var flags = BitConverter.ToUInt16(peBytes, bodyOffset);
        headerSize = ((flags >> 12) & 0xF) * 4;
        codeSize = BitConverter.ToInt32(peBytes, bodyOffset + 4);
    }
    else
    {
        throw new InvalidOperationException($"Unknown method header format: {displayName}");
    }

    if (codeSize < 1)
    {
        throw new InvalidOperationException($"Method body too small to patch: {displayName}");
    }

    var ilStart = bodyOffset + headerSize;

    peBytes[ilStart + 0] = 0x2A; // ret

    for (var i = 1; i < codeSize; i++)
    {
        peBytes[ilStart + i] = 0x00; // nop padding
    }
}

static int GetOffset(PEReader peReader, int relativeVirtualAddress)
{
    var headers = peReader.PEHeaders;

    foreach (var section in headers.SectionHeaders)
    {
        var start = section.VirtualAddress;
        var size = Math.Max(section.VirtualSize, section.SizeOfRawData);
        var end = start + size;

        if (relativeVirtualAddress >= start && relativeVirtualAddress < end)
        {
            return relativeVirtualAddress - start + section.PointerToRawData;
        }
    }

    throw new InvalidOperationException($"RVA not mapped to a section: 0x{relativeVirtualAddress:X}");
}
