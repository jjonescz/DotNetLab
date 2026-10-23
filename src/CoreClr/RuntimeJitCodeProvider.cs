using System.Diagnostics.Tracing;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace DotNetLab;

internal sealed class RuntimeJitCodeProvider : EventListener
{
    private readonly Dictionary<ulong, JitCode> methods = new();

    public RuntimeJitCodeProvider()
    {
        foreach (var source in EventSource.GetSources())
        {
            OnEventSourceCreated(source);
        }
    }

    public JitCode GetCode(MethodBase method)
    {
        var handle = method.MethodHandle;
        RuntimeHelpers.PrepareMethod(handle);
        ulong entry = RuntimeEntryPoint.Resolve(method, (ulong)handle.GetFunctionPointer());
        if (method is MethodInfo { IsVirtual: true, DeclaringType.IsValueType: true } info)
        {
            // ldftn resolves the non-unboxing entry without calling the user's method.
            var pointer = new DynamicMethod("GetEntryPoint", typeof(nint), [], typeof(RuntimeJitCodeProvider).Module, skipVisibility: true);
            var il = pointer.GetILGenerator();
            il.Emit(OpCodes.Ldftn, info);
            il.Emit(OpCodes.Ret);
            entry = (ulong)pointer.CreateDelegate<Func<nint>>()();
        }

        ulong id = (ulong)handle.Value;
        bool runtimeAsync = method.MethodImplementationFlags.HasFlag(MethodImplAttributes.Async);
        long start = Stopwatch.GetTimestamp();
        var timeout = TimeSpan.FromSeconds(10);

        lock (methods)
        {
            while (true)
            {
                var code = methods.GetValueOrDefault(entry) ?? methods.Values.FirstOrDefault(m => m.MethodId == id);
                if (code is not null)
                {
                    if (!runtimeAsync)
                    {
                        return code;
                    }

                    // Reflection exposes the Task adapter, not the runtime-async body.
                    var body = methods.Values.FirstOrDefault(m =>
                        m.ModuleId == code.ModuleId && m.Token == code.Token && m.MethodId != code.MethodId);
                    if (body is not null)
                    {
                        return body;
                    }
                }

                var remaining = timeout - Stopwatch.GetElapsedTime(start);
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException($"No JIT {(runtimeAsync ? "async body" : "code")} was reported for '{method}' (entry 0x{entry:x}, handle 0x{id:x}). Ensure runtime diagnostics are enabled.");
                }

                Monitor.Wait(methods, remaining);
            }
        }
    }

    public Dictionary<ulong, string> GetSymbols()
    {
        lock (methods)
        {
            return methods.Values.GroupBy(m => m.Address).ToDictionary(g => g.Key, g => g.First().Name);
        }
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
        {
            EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)0x10); // JIT
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        // EventListener can call overrides before our constructor initializes the dictionary.
        if (methods is null || eventData.EventName != "MethodLoadVerbose_V2" ||
            GetPayload(eventData, "MethodID") is not ulong id ||
            GetPayload(eventData, "ModuleID") is not ulong module ||
            GetPayload(eventData, "MethodToken") is not uint token ||
            GetPayload(eventData, "MethodStartAddress") is not ulong address ||
            GetPayload(eventData, "MethodSize") is not uint size || address == 0 || size == 0)
        {
            return;
        }

        var name = $"{GetPayload(eventData, "MethodNamespace")}.{GetPayload(eventData, "MethodName")}";
        lock (methods)
        {
            methods[address] = new(id, module, token, address, checked((int)size), name);
            Monitor.PulseAll(methods);
        }
    }

    private static object? GetPayload(EventWrittenEventArgs data, string name)
    {
        int index = data.PayloadNames?.IndexOf(name) ?? -1;
        return index >= 0 ? data.Payload?[index] : null;
    }
}

internal sealed record JitCode(ulong MethodId, ulong ModuleId, uint Token, ulong Address, int Size, string Name);
