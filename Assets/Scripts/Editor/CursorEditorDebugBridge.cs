using System;
using System.Collections.Concurrent;
using System.Globalization;
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
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        EditorApplication.quitting += OnEditorQuitting;
        EditorApplication.delayCall += DelayedStartListener;
    }

    [InitializeOnLoadMethod]
    private static void InitializeBridgeListener()
    {
        EditorApplication.delayCall -= DelayedStartListener;
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
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        StopListener();
    }

    private static void OnEditorQuitting()
    {
        EditorApplication.update -= OnEditorUpdate;
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        StopListener();
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredPlayMode || state == PlayModeStateChange.EnteredEditMode)
        {
            EditorApplication.delayCall -= DelayedStartListener;
            EditorApplication.delayCall += DelayedStartListener;
        }
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
            case "invoke_static_json":
            {
                return ExecuteStaticJsonInvoke(req);
            }
            default:
                return ToJsonError(req.id, "unknown_cmd", cmd);
        }
    }

    private static string ExecuteStaticJsonInvoke(BridgeRequest req)
    {
        string typeName = req.args != null ? req.args.typeName : null;
        string methodName = req.args != null ? req.args.methodName : null;
        JsonParameter[] suppliedParameters = req.args != null ? req.args.parameters : null;
        if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(methodName))
        {
            return ToJsonError(
                req.id,
                "bad_args",
                "invoke_static_json requires args.typeName, args.methodName, and optional args.parameters");
        }

        if (suppliedParameters == null)
        {
            suppliedParameters = new JsonParameter[0];
        }

        Type targetType = ResolveEditorType(typeName);
        if (targetType == null)
        {
            return ToJsonError(req.id, "type_not_found", typeName);
        }

        MethodInfo[] methods = targetType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo matchedMethod = null;
        object[] matchedArgs = null;
        int matchCount = 0;
        string lastError = "";

        for (int i = 0; i < methods.Length; i++)
        {
            MethodInfo method = methods[i];
            if (method.Name != methodName)
            {
                continue;
            }

            ParameterInfo[] parameterInfos = method.GetParameters();
            if (parameterInfos.Length != suppliedParameters.Length)
            {
                continue;
            }

            object[] converted = new object[parameterInfos.Length];
            bool canUse = true;
            for (int p = 0; p < parameterInfos.Length; p++)
            {
                if (!TryConvertJsonParameter(
                        suppliedParameters[p],
                        parameterInfos[p].ParameterType,
                        out converted[p],
                        out string error))
                {
                    canUse = false;
                    lastError = $"Parameter {p} for {method.Name}: {error}";
                    break;
                }
            }

            if (!canUse)
            {
                continue;
            }

            matchedMethod = method;
            matchedArgs = converted;
            matchCount++;
        }

        if (matchCount == 0)
        {
            string message = string.IsNullOrEmpty(lastError)
                ? $"No static overload '{methodName}' on '{targetType.FullName}' accepts {suppliedParameters.Length} parameter(s)"
                : lastError;
            return ToJsonError(req.id, "method_not_found", message);
        }

        if (matchCount > 1)
        {
            return ToJsonError(
                req.id,
                "ambiguous_method",
                $"Multiple static overloads of '{methodName}' on '{targetType.FullName}' accept the supplied JSON parameters");
        }

        try
        {
            object returnValue = matchedMethod.Invoke(null, matchedArgs);
            var response = new BridgeStaticJsonInvokeOkResponse
            {
                id = req.id,
                ok = true,
                result = new StaticJsonInvokeResult
                {
                    typeName = targetType.FullName,
                    methodName = matchedMethod.Name,
                    parameterCount = suppliedParameters.Length,
                    invoked = true,
                    returnType = matchedMethod.ReturnType == typeof(void) ? "void" : matchedMethod.ReturnType.FullName,
                    returnValue = matchedMethod.ReturnType == typeof(void) ? "" : ConvertReturnValueToString(returnValue),
                },
            };
            return JsonUtility.ToJson(response);
        }
        catch (Exception e)
        {
            return ToJsonError(req.id, "invoke_exception", e.InnerException != null ? e.InnerException.Message : e.Message);
        }
    }

    private static bool TryConvertJsonParameter(JsonParameter parameter, Type targetType, out object value, out string error)
    {
        value = null;
        error = "";

        if (parameter == null || string.Equals(parameter.type, "null", StringComparison.OrdinalIgnoreCase))
        {
            if (!targetType.IsValueType || Nullable.GetUnderlyingType(targetType) != null)
            {
                return true;
            }

            error = $"Cannot assign null to value type {targetType.FullName}";
            return false;
        }

        Type nullableType = Nullable.GetUnderlyingType(targetType);
        if (nullableType != null)
        {
            targetType = nullableType;
        }

        if (targetType.IsArray)
        {
            return TryConvertJsonArray(parameter, targetType, out value, out error);
        }

        if (targetType == typeof(string))
        {
            value = parameter.value ?? "";
            return true;
        }

        if (targetType == typeof(bool))
        {
            return TryParseBool(parameter.value, out value, out error);
        }

        if (targetType == typeof(int))
        {
            return TryParseInt(parameter.value, out value, out error);
        }

        if (targetType == typeof(long))
        {
            return TryParseLong(parameter.value, out value, out error);
        }

        if (targetType == typeof(float))
        {
            return TryParseFloat(parameter.value, out value, out error);
        }

        if (targetType == typeof(double))
        {
            return TryParseDouble(parameter.value, out value, out error);
        }

        if (targetType == typeof(Vector2))
        {
            value = new Vector2(parameter.x, parameter.y);
            return true;
        }

        if (targetType == typeof(Vector3))
        {
            value = new Vector3(parameter.x, parameter.y, parameter.z);
            return true;
        }

        if (targetType == typeof(Vector4))
        {
            value = new Vector4(parameter.x, parameter.y, parameter.z, parameter.w);
            return true;
        }

        if (targetType == typeof(Color))
        {
            value = new Color(parameter.r, parameter.g, parameter.b, parameter.a);
            return true;
        }

        error = $"Unsupported target parameter type {targetType.FullName}";
        return false;
    }

    private static bool TryConvertJsonArray(JsonParameter parameter, Type targetType, out object value, out string error)
    {
        value = null;
        error = "";

        Type elementType = targetType.GetElementType();
        if (elementType == null)
        {
            error = $"Cannot resolve array element type for {targetType.FullName}";
            return false;
        }

        if (parameter.values == null)
        {
            error = "Array parameter requires values";
            return false;
        }

        Array valuesArray = Array.CreateInstance(elementType, parameter.values.Length);
        for (int i = 0; i < parameter.values.Length; i++)
        {
            var item = new JsonParameter
            {
                type = elementType.Name,
                value = parameter.values[i],
            };
            if (!TryConvertJsonParameter(item, elementType, out object converted, out string itemError))
            {
                error = $"Array value {i}: {itemError}";
                return false;
            }

            valuesArray.SetValue(converted, i);
        }

        value = valuesArray;
        return true;
    }

    private static bool TryParseBool(string raw, out object value, out string error)
    {
        value = null;
        error = "";
        if (bool.TryParse(raw, out bool parsed))
        {
            value = parsed;
            return true;
        }

        error = $"Expected bool value, got '{raw}'";
        return false;
    }

    private static bool TryParseInt(string raw, out object value, out string error)
    {
        value = null;
        error = "";
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            value = parsed;
            return true;
        }

        error = $"Expected int value, got '{raw}'";
        return false;
    }

    private static bool TryParseLong(string raw, out object value, out string error)
    {
        value = null;
        error = "";
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
        {
            value = parsed;
            return true;
        }

        error = $"Expected long value, got '{raw}'";
        return false;
    }

    private static bool TryParseFloat(string raw, out object value, out string error)
    {
        value = null;
        error = "";
        if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
        {
            value = parsed;
            return true;
        }

        error = $"Expected float value, got '{raw}'";
        return false;
    }

    private static bool TryParseDouble(string raw, out object value, out string error)
    {
        value = null;
        error = "";
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            value = parsed;
            return true;
        }

        error = $"Expected double value, got '{raw}'";
        return false;
    }

    private static string ConvertReturnValueToString(object returnValue)
    {
        if (returnValue == null)
        {
            return "null";
        }

        IFormattable formattable = returnValue as IFormattable;
        if (formattable != null)
        {
            return formattable.ToString(null, CultureInfo.InvariantCulture);
        }

        return returnValue.ToString();
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
        public JsonParameter[] parameters;
    }

    [Serializable]
    private class JsonParameter
    {
        public string type;
        public string value;
        public string[] values;
        public float x;
        public float y;
        public float z;
        public float w;
        public float r;
        public float g;
        public float b;
        public float a = 1.0f;
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
    private class StaticJsonInvokeResult
    {
        public string typeName;
        public string methodName;
        public int parameterCount;
        public bool invoked;
        public string returnType;
        public string returnValue;
    }

    [Serializable]
    private class BridgeInvokeOkResponse
    {
        public string id;
        public bool ok;
        public InvokeStaticResult result;
    }

    [Serializable]
    private class BridgeStaticJsonInvokeOkResponse
    {
        public string id;
        public bool ok;
        public StaticJsonInvokeResult result;
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
