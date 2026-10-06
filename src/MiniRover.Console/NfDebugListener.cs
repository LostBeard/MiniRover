using System.Reflection;

namespace MiniRover.ConsoleApp;

/// <summary>
/// Listens to a nanoFramework device's Debug.WriteLine output over its serial wire protocol, using the
/// nanoFramework debugger library (NF_DEBUG_LIBRARY = path to nanoFramework.Tools.DebugLibrary.Net.dll from the
/// nanoFramework Visual Studio extension). Development/test aid only: lets a test read the setup code the car
/// prints when the LED eyes are not visible (board out of the car).
/// </summary>
public sealed class NfDebugListener : IDisposable
{
    public event Action<string>? Line;
    object? _engine;

    public static NfDebugListener Attach(string port)
    {
        string dll = Environment.GetEnvironmentVariable("NF_DEBUG_LIBRARY")
                     ?? throw new InvalidOperationException("set NF_DEBUG_LIBRARY to nanoFramework.Tools.DebugLibrary.Net.dll");
        Assembly asm = Assembly.LoadFrom(dll);
        Type portBaseType = asm.GetType("nanoFramework.Tools.Debugger.PortBase")!;
        object portBase = portBaseType.GetMethod("CreateInstanceForSerial", [typeof(bool)])!.Invoke(null, [false])!;
        object device = portBaseType.GetMethod("AddDevice", [typeof(string)])!.Invoke(portBase, [port])
                        ?? throw new InvalidOperationException($"no nanoFramework device on {port}");
        object engine = device.GetType().GetProperty("DebugEngine")!.GetValue(device)!;
        Type engineType = engine.GetType();

        var listener = new NfDebugListener { _engine = engine };
        EventInfo onMessage = engineType.GetEvent("OnMessage")!;
        Action<object, string> adapter = (_, text) => listener.Raise(text);
        onMessage.AddEventHandler(engine, Delegate.CreateDelegate(onMessage.EventHandlerType!, adapter.Target, adapter.Method));

        MethodInfo connect = engineType.GetMethods().First(m => m.Name == "Connect" && m.GetParameters().Length == 3
                                                                 && m.GetParameters()[0].ParameterType == typeof(int));
        if (!(bool)connect.Invoke(engine, [5000, true, true])!) throw new InvalidOperationException($"debugger connect to {port} failed");
        return listener;
    }

    // Debug output arrives in fragments; re-assemble lines.
    readonly System.Text.StringBuilder _partial = new();

    void Raise(string text)
    {
        lock (_partial)
        {
            _partial.Append(text);
            string all = _partial.ToString();
            int nl;
            while ((nl = all.IndexOf('\n')) >= 0)
            {
                Line?.Invoke(all[..nl].TrimEnd('\r'));
                all = all[(nl + 1)..];
            }
            _partial.Clear().Append(all);
        }
    }

    public void Dispose()
    {
        try { _engine?.GetType().GetMethod("Stop", Type.EmptyTypes)?.Invoke(_engine, null); } catch { }
        (_engine as IDisposable)?.Dispose();
    }
}
