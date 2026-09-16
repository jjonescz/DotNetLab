// From https://github.com/compiler-explorer/dotnet-builder/blob/68da85ff965d2c784a0eaa0ce6f14f4d9692144c/build/DisassemblyLoader/MonoInterop.cs.

using System.Runtime.InteropServices;

namespace DotNetLab;

internal static class MonoInterop
{
    private const string MonoLib = "libmonosgen-2.0.so";

    [DllImport(MonoLib, ExactSpelling = true)]
    public static extern void* mono_domain_get();

    [DllImport(MonoLib, ExactSpelling = true)]
    public static extern void* mono_compile_method(void* method);

    [DllImport(MonoLib, ExactSpelling = true)]
    public static extern void* mono_jit_info_table_find(void* domain, void* addr);

    [DllImport(MonoLib, ExactSpelling = true)]
    public static extern void* mono_jit_info_get_code_start(void* ji);

    [DllImport(MonoLib, ExactSpelling = true)]
    public static extern int mono_jit_info_get_code_size(void* ji);

    [DllImport(MonoLib, ExactSpelling = true)]
    public static extern void* mono_pmip(void* ip);

    [DllImport(MonoLib, ExactSpelling = true)]
    public static extern void mono_free(void* ptr);
}
