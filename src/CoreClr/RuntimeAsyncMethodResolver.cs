using JitInspect;
using Microsoft.Diagnostics.Runtime;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;

namespace DotNetLab;

internal sealed class RuntimeAsyncMethodResolver : EventListener
{
    private readonly Dictionary<int, PendingMethod> methods;
    private readonly ulong moduleId;

    private RuntimeAsyncMethodResolver(ulong moduleId, MethodInfo[] methods)
    {
        this.methods = methods.ToDictionary(m => m.MetadataToken, m => new PendingMethod(m.MethodHandle));
        this.moduleId = moduleId;

        foreach (var source in EventSource.GetSources())
        {
            OnEventSourceCreated(source);
        }
    }

    public static MethodInfo[] GetMethods(Assembly assembly)
    {
        var methods = assembly.DefinedTypes.SelectMany(t => t.DeclaredMethods)
            .Where(m => m.MethodImplementationFlags.HasFlag(MethodImplAttributes.Async) &&
                !m.ContainsGenericParameters && !m.IsAbstract)
            .ToArray();

        // Linux snapshots cannot see method handles materialized after JitDisassembler.Create().
        foreach (var method in methods)
        {
            _ = method.MethodHandle;
        }

        return methods;
    }

    public static RuntimeAsyncMethodResolver? Create(JitDisassembler disassembler, MethodInfo[] methods)
    {
        if (methods.Length == 0)
        {
            return null;
        }

        var runtime = GetRuntime(disassembler);
        var method = runtime.GetMethodByHandle((ulong)methods[0].MethodHandle.Value)
            ?? throw new InvalidOperationException("Cannot locate the runtime-async assembly.");
        return new RuntimeAsyncMethodResolver(method.Type.Module.Address, methods);
    }

    public MethodBase Resolve(MethodBase method)
    {
        if (!methods.TryGetValue(method.MetadataToken, out var pending))
        {
            return method;
        }

        // PrepareMethod compiles both the Task adapter and the async body without executing either.
        // Reflection exposes only the adapter; the JIT event provides the body's actual handle.
        RuntimeHelpers.PrepareMethod(method.MethodHandle);
        if (!pending.Body.Task.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException($"No runtime-async JIT body was reported for '{method}'. Ensure runtime diagnostics are enabled.");
        }

        return new AsyncBodyMethod(method, RuntimeMethodHandle.FromIntPtr((nint)pending.Body.Task.Result));
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        // EventListener calls this override during its base constructor, before our fields are initialized.
        if (moduleId != 0 && eventSource.Name == "Microsoft-Windows-DotNETRuntime")
        {
            EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)0x10); // JIT keyword
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventName != "MethodLoadVerbose_V2" ||
            GetPayload(eventData, "ModuleID") is not ulong module || module != moduleId ||
            GetPayload(eventData, "MethodToken") is not uint token ||
            GetPayload(eventData, "MethodID") is not ulong handle ||
            !methods.TryGetValue((int)token, out var pending) ||
            handle == (ulong)pending.Adapter.Value)
        {
            return;
        }

        pending.Body.TrySetResult(handle);
    }

    private static object? GetPayload(EventWrittenEventArgs data, string name)
    {
        int index = data.PayloadNames?.IndexOf(name) ?? -1;
        return index >= 0 ? data.Payload?[index] : null;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "runtime")]
    private static extern ref ClrRuntime GetRuntime(JitDisassembler disassembler);

    private sealed class PendingMethod(RuntimeMethodHandle adapter)
    {
        public RuntimeMethodHandle Adapter { get; } = adapter;
        public TaskCompletionSource<ulong> Body { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class AsyncBodyMethod(MethodBase original, RuntimeMethodHandle handle) : MethodBase
    {
        public override RuntimeMethodHandle MethodHandle => handle;
        // The body handle is already resolved, so JitInspect must not use its virtual-method ldftn workaround.
        public override MethodAttributes Attributes => original.Attributes & ~MethodAttributes.Virtual;
        public override MemberTypes MemberType => original.MemberType;
        public override Module Module => original.Module;
        public override int MetadataToken => original.MetadataToken;
        public override CallingConventions CallingConvention => original.CallingConvention;
        public override Type? DeclaringType => original.DeclaringType;
        public override Type? ReflectedType => original.ReflectedType;
        public override string Name => original.Name;
        public override MethodImplAttributes GetMethodImplementationFlags() => original.GetMethodImplementationFlags();
        public override ParameterInfo[] GetParameters() => original.GetParameters();
        public override object[] GetCustomAttributes(bool inherit) => original.GetCustomAttributes(inherit);
        public override object[] GetCustomAttributes(Type attributeType, bool inherit) => original.GetCustomAttributes(attributeType, inherit);
        public override bool IsDefined(Type attributeType, bool inherit) => original.IsDefined(attributeType, inherit);
        public override object? Invoke(object? obj, BindingFlags invokeAttr, Binder? binder, object?[]? parameters, CultureInfo? culture) =>
            throw new NotSupportedException("Runtime-async bodies are exposed only for disassembly, not invocation.");
    }
}
