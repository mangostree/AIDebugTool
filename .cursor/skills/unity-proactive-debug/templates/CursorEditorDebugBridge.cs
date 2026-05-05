using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

/// <summary>
/// Localhost TCP bridge for Cursor/Python: JSON one line per request/response.
/// Default 127.0.0.1:8742. Matches .cursor/skills/unity-proactive-debug/reference.md
/// </summary>
[InitializeOnLoad]
public static class CursorEditorDebugBridge
{
    private const int DefaultPort = 8742;
    private static readonly ConcurrentQueue<PendingWork> s_Pending = new ConcurrentQueue<PendingWork>();
    private static TcpListener s_Listener;
    private static Thread s_AcceptThread;
    private static volatile bool s_WantRun;

    static CursorEditorDebugBridge()
    {
        s_WantRun = true;
        EditorApplication.update += OnEditorUpdate;
        AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        EditorApplication.quitting += OnEditorQuitting;
        EditorApplication.delayCall += DelayedStartListener;
    }

    private static void DelayedStartListener()
    {
        EditorApplication.delayCall -= DelayedStartListener;
        StartListener();
    }

    private static void StartListener()
    {
        if (s_Listener != null)
        {
            return;
        }

        try
        {
            s_WantRun = true;
            s_Listener = new TcpListener(IPAddress.Loopback, DefaultPort);
            s_Listener.ExclusiveAddressUse = false;
            s_Listener.Start();
            s_AcceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "CursorEditorDebugBridge",
            };
            s_AcceptThread.Start();
            Debug.Log($"[CursorEditorDebugBridge] Listening on 127.0.0.1:{DefaultPort}");
        }
        catch (Exception e)
        {
            s_Listener = null;
            s_AcceptThread = null;
            Debug.LogError(
                $"[CursorEditorDebugBridge] Failed to start on port {DefaultPort}: {e.Message}. " +
                "Close other Unity Editors using this project or another process on 8742.");
        }
    }

    private static void StopListener()
    {
        s_WantRun = false;
        TcpListener listener = s_Listener;
        s_Listener = null;
        if (listener != null)
        {
            try
            {
                listener.Stop();
            }
            catch (Exception)
            {
                // ignore
            }

            try
            {
                ((IDisposable)listener).Dispose();
            }
            catch (Exception)
            {
                // ignore
            }
        }

        if (s_AcceptThread != null && s_AcceptThread.IsAlive)
        {
            if (!s_AcceptThread.Join(2000))
            {
                Debug.LogWarning("[CursorEditorDebugBridge] Accept thread did not exit in time.");
            }
        }

        s_AcceptThread = null;
    }

    private static void OnBeforeAssemblyReload()
    {
        EditorApplication.update -= OnEditorUpdate;
        StopListener();
    }

    private static void OnEditorQuitting()
    {
        EditorApplication.update -= OnEditorUpdate;
        StopListener();
    }

    private static void AcceptLoop()
    {
        while (s_WantRun && s_Listener != null)
        {
            try
            {
                var client = s_Listener.AcceptTcpClient();
                if (client == null)
                {
                    continue;
                }

                try
                {
                    NetworkStream stream = client.GetStream();
                    string line = ReadLine(stream);
                    if (string.IsNullOrEmpty(line))
                    {
                        client.Close();
                        continue;
                    }

                    s_Pending.Enqueue(new PendingWork(client, line));
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[CursorEditorDebugBridge] Read error: {e.Message}");
                    try
                    {
                        client.Close();
                    }
                    catch (Exception)
                    {
                        // ignore
                    }
                }
            }
            catch (SocketException)
            {
                if (!s_WantRun)
                {
                    break;
                }
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                break;
            }
        }
    }

    private static string ReadLine(NetworkStream stream)
    {
        var buffer = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            int n = stream.Read(one, 0, 1);
            if (n == 0)
            {
                break;
            }

            if (one[0] == (byte)'\n')
            {
                break;
            }

            buffer.WriteByte(one[0]);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void OnEditorUpdate()
    {
        if (!s_Pending.TryDequeue(out PendingWork work))
        {
            return;
        }

        string responseJson;
        try
        {
            var req = JsonUtility.FromJson<BridgeRequest>(work.Line);
            if (req == null || string.IsNullOrEmpty(req.cmd))
            {
                responseJson = ToJsonError("", "bad_request", "Missing cmd");
            }
            else
            {
                string token = EditorPrefs.GetString("CursorEditorBridge.AuthToken", "");
                if (!string.IsNullOrEmpty(token))
                {
                    if (string.IsNullOrEmpty(req.auth) || req.auth != token)
                    {
                        responseJson = ToJsonError(req.id, "unauthorized", "Invalid or missing auth");
                    }
                    else
                    {
                        responseJson = ExecuteCommand(req);
                    }
                }
                else
                {
                    responseJson = ExecuteCommand(req);
                }
            }
        }
        catch (Exception e)
        {
            responseJson = ToJsonError("", "exception", e.Message);
        }

        try
        {
            using (var stream = work.Client.GetStream())
            {
                byte[] outBytes = Encoding.UTF8.GetBytes(responseJson + "\n");
                stream.Write(outBytes, 0, outBytes.Length);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[CursorEditorDebugBridge] Write error: {e.Message}");
        }
        finally
        {
            try
            {
                work.Client.Close();
            }
            catch (Exception)
            {
                // ignore
            }
        }
    }

    private static PingResult MakeStatus()
    {
        return new PingResult
        {
            unityVersion = Application.unityVersion,
            isPlaying = EditorApplication.isPlaying,
            isCompiling = EditorApplication.isCompiling,
        };
    }

    private static string ExecuteCommand(BridgeRequest req)
    {
        string cmd = req.cmd.Trim().ToLowerInvariant();
        switch (cmd)
        {
            case "ping":
            {
                var res = new BridgeOkResponse
                {
                    id = req.id,
                    ok = true,
                    result = MakeStatus(),
                };
                return JsonUtility.ToJson(res);
            }
            case "compile_status":
            {
                var res = new BridgeOkResponse
                {
                    id = req.id,
                    ok = true,
                    result = MakeStatus(),
                };
                return JsonUtility.ToJson(res);
            }
            case "refresh":
            {
                AssetDatabase.Refresh(ImportAssetOptions.Default);
                CompilationPipeline.RequestScriptCompilation(RequestScriptCompilationOptions.None);
                var res = new BridgeOkResponse
                {
                    id = req.id,
                    ok = true,
                    result = MakeStatus(),
                };
                return JsonUtility.ToJson(res);
            }
            case "enter_play":
            {
                if (EditorApplication.isPlaying)
                {
                    var res = new BridgeOkResponse
                    {
                        id = req.id,
                        ok = true,
                        result = MakeStatus(),
                    };
                    return JsonUtility.ToJson(res);
                }

                EditorApplication.EnterPlaymode();
                var ok = new BridgeOkResponse
                {
                    id = req.id,
                    ok = true,
                    result = MakeStatus(),
                };
                return JsonUtility.ToJson(ok);
            }
            case "exit_play":
            {
                if (!EditorApplication.isPlaying)
                {
                    var res = new BridgeOkResponse
                    {
                        id = req.id,
                        ok = true,
                        result = MakeStatus(),
                    };
                    return JsonUtility.ToJson(res);
                }

                EditorApplication.ExitPlaymode();
                var ok = new BridgeOkResponse
                {
                    id = req.id,
                    ok = true,
                    result = MakeStatus(),
                };
                return JsonUtility.ToJson(ok);
            }
            case "menu":
            {
                string path = req.args != null ? req.args.path : null;
                if (string.IsNullOrEmpty(path))
                {
                    return ToJsonError(req.id, "bad_args", "menu requires args.path (Unity menu path, use / separators)");
                }

                path = path.Replace('\\', '/').Trim();
                bool executed = EditorApplication.ExecuteMenuItem(path);
                var menuRes = new BridgeMenuOkResponse
                {
                    id = req.id,
                    ok = true,
                    result = new MenuInvokeResult
                    {
                        menuPath = path,
                        executed = executed,
                    },
                };
                return JsonUtility.ToJson(menuRes);
            }
            case "invoke_static":
            {
                string typeName = req.args != null ? req.args.typeName : null;
                string methodName = req.args != null ? req.args.methodName : null;
                if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(methodName))
                {
                    return ToJsonError(
                        req.id,
                        "bad_args",
                        "invoke_static requires args.typeName (full name) and args.methodName (static, parameterless)");
                }

                Type t = ResolveEditorType(typeName);
                if (t == null)
                {
                    return ToJsonError(req.id, "type_not_found", typeName);
                }

                MethodInfo mi = t.GetMethod(
                    methodName,
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null);
                if (mi == null)
                {
                    return ToJsonError(
                        req.id,
                        "method_not_found",
                        $"No static parameterless method '{methodName}' on '{t.FullName}'");
                }

                try
                {
                    mi.Invoke(null, null);
                }
                catch (Exception e)
                {
                    return ToJsonError(req.id, "invoke_exception", e.InnerException != null ? e.InnerException.Message : e.Message);
                }

                var invRes = new BridgeInvokeOkResponse
                {
                    id = req.id,
                    ok = true,
                    result = new InvokeStaticResult
                    {
                        typeName = t.FullName,
                        methodName = methodName,
                        invoked = true,
                    },
                };
                return JsonUtility.ToJson(invRes);
            }
            default:
                return ToJsonError(req.id, "unknown_cmd", cmd);
        }
    }

    private static Type ResolveEditorType(string fullName)
    {
        if (string.IsNullOrEmpty(fullName))
        {
            return null;
        }

        Type direct = Type.GetType(fullName);
        if (direct != null)
        {
            return direct;
        }

        foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                Type t = asm.GetType(fullName);
                if (t != null)
                {
                    return t;
                }
            }
            catch (Exception)
            {
                // skip
            }
        }

        foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                foreach (Type type in asm.GetTypes())
                {
                    if (type.FullName == fullName)
                    {
                        return type;
                    }
                }
            }
            catch (ReflectionTypeLoadException e)
            {
                if (e.Types != null)
                {
                    foreach (Type lt in e.Types)
                    {
                        if (lt != null && lt.FullName == fullName)
                        {
                            return lt;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // skip assembly
            }
        }

        return null;
    }

    private static string ToJsonError(string id, string error, string message)
    {
        var err = new BridgeErrResponse
        {
            id = id ?? "",
            ok = false,
            error = error,
            message = message,
        };
        return JsonUtility.ToJson(err);
    }

    [Serializable]
    private class BridgeArgs
    {
        public string path;
        public string typeName;
        public string methodName;
    }

    [Serializable]
    private class BridgeRequest
    {
        public string id;
        public string cmd;
        public string auth;
        public BridgeArgs args;
    }

    [Serializable]
    private class MenuInvokeResult
    {
        public string menuPath;
        public bool executed;
    }

    [Serializable]
    private class BridgeMenuOkResponse
    {
        public string id;
        public bool ok;
        public MenuInvokeResult result;
    }

    [Serializable]
    private class InvokeStaticResult
    {
        public string typeName;
        public string methodName;
        public bool invoked;
    }

    [Serializable]
    private class BridgeInvokeOkResponse
    {
        public string id;
        public bool ok;
        public InvokeStaticResult result;
    }

    [Serializable]
    private class PingResult
    {
        public string unityVersion;
        public bool isPlaying;
        public bool isCompiling;
    }

    [Serializable]
    private class BridgeOkResponse
    {
        public string id;
        public bool ok;
        public PingResult result;
    }

    [Serializable]
    private class BridgeErrResponse
    {
        public string id;
        public bool ok;
        public string error;
        public string message;
    }

    private readonly struct PendingWork
    {
        public readonly TcpClient Client;
        public readonly string Line;

        public PendingWork(TcpClient client, string line)
        {
            Client = client;
            Line = line;
        }
    }
}
