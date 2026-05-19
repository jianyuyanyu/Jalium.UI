using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Jalium.UI.Notifications;

namespace Jalium.UI.Desktop.Platforms.Windows;

/// <summary>
/// Windows notification backend using raw COM vtable calls to the WinRT
/// <c>Windows.UI.Notifications</c> APIs. No UWP/WinAppSDK/Toolkit dependency.
/// Lives in <c>Jalium.UI.Desktop</c> so the cross-platform Controls assembly
/// stays free of Win32 entanglement; <c>DesktopBootstrap</c> registers it
/// with <see cref="SystemNotificationManager.BackendFactory"/>.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class WindowsNotificationBackend : INotificationBackend
{
    private string _appId = string.Empty;
    private nint _notifierPtr;
    private bool _disposed;
    private bool _runtimeAvailable;
    private Exception? _initializationFailure;
    private readonly Dictionary<uint, NotificationHandle> _activeNotifications = new();
    private uint _nextId;

    private static bool OsVersionSupported =>
        OperatingSystem.IsWindows() && Environment.OSVersion.Version >= new Version(10, 0, 10240);

    /// <summary>
    /// True only when the OS is recent enough <em>and</em> the toast notifier
    /// was successfully created. Returns false on systems where the WinRT
    /// notification platform refuses to activate (Server Core, GPO-disabled,
    /// Session 0 services, etc.).
    /// </summary>
    public bool IsSupported => OsVersionSupported && _runtimeAvailable;

    /// <summary>
    /// If <see cref="IsSupported"/> is false because initialization failed
    /// at runtime, this returns the underlying exception. Otherwise null.
    /// </summary>
    public Exception? InitializationFailure => _initializationFailure;

    #region INotificationBackend

    public void Initialize(string appId, string appName)
    {
        if (!OsVersionSupported)
            return;

        _appId = appId;

        // Whole pipeline is HRESULT-based on purpose: WPN_E_PLATFORM_UNAVAILABLE
        // is an *expected* environmental outcome (Server Core, GPO-disabled, fresh
        // install where the AUMID isn't yet associated, etc.). Throwing a
        // COMException here would (a) tear down the calling page and (b) trip
        // Visual Studio's "break on thrown" debugger setting. We translate hr
        // into Exception only for the diagnostics field, never on the live path.
        int hr = TryInitializeRuntime(appId, appName);
        if (hr < 0)
        {
            _initializationFailure = BuildInitFailureException(hr);
            ResetNativeState();
            return;
        }

        _runtimeAvailable = true;
    }

    private static Exception BuildInitFailureException(int hr)
    {
        var inner = Marshal.GetExceptionForHR(hr) ?? new InvalidOperationException($"HRESULT 0x{hr:X8}");

        // Augment 0x803E0105 (WPN_E_PLATFORM_UNAVAILABLE) with the most common
        // user-fixable cause: the per-user push notifications service is stopped.
        // wpnapps.dll cannot validate the toast activator CLSID without it and
        // surfaces it as "platform unavailable" with no further context.
        if (hr == unchecked((int)0x803E0105))
        {
            return new InvalidOperationException(
                "Windows 推送通知服务 (WpnUserService) 不可用,toast 通知无法发送。" +
                "请尝试: 1) 在 PowerShell 运行 Start-Service 'WpnUserService_*';" +
                " 2) 设置 → 系统 → 通知 → 启用应用通知; 3) 重启 Windows。",
                inner);
        }
        return inner;
    }

    private int TryInitializeRuntime(string appId, string appName)
    {
        int hr = WinRT.TryEnsureInitialized();
        if (hr < 0) return hr;

        // Win10 RS5+ refuses CreateToastNotifierWithId with
        // WPN_E_PLATFORM_UNAVAILABLE unless **all four** of these are in place:
        //   1. Start-Menu .lnk with PKEY_AppUserModel_ID
        //   2. Start-Menu .lnk with PKEY_AppUserModel_ToastActivatorCLSID
        //   3. HKCU\Software\Classes\CLSID\{clsid}\LocalServer32 pointing at *some* exe
        //      — wpnapps.dll!util.cpp validates the CLSID is a real COM server,
        //      and a missing LocalServer32 surfaces as 0x80040154 (REGDB_E_CLASSNOTREG)
        //      which the platform converts to 0x803E0105.
        //   4. SetCurrentProcessExplicitAppUserModelID matching (1).
        Guid activatorClsid = DeriveActivatorClsid(appId);
        EnsureToastActivatorRegistered(activatorClsid, appName);
        EnsureShortcut(appId, appName, activatorClsid);
        TrySetCurrentProcessAumid(appId);

        return TryCreateNotifier(_appId, out _notifierPtr);
    }

    private static int TryCreateNotifier(string appId, out nint notifierPtr)
    {
        notifierPtr = 0;

        int hr = WinRT.TryCreateHString("Windows.UI.Notifications.ToastNotificationManager", out nint hClassName);
        if (hr < 0) return hr;

        try
        {
            var managerIid = WinRT.IID_IToastNotificationManagerStatics;
            hr = WinRT.RoGetActivationFactory(hClassName, ref managerIid, out nint managerPtr);
            if (hr < 0) return hr;

            try
            {
                hr = WinRT.TryCreateHString(appId, out nint hAppId);
                if (hr < 0) return hr;

                try
                {
                    hr = WinRT.IToastNotificationManagerStatics_CreateToastNotifierWithId(
                        managerPtr, hAppId, out notifierPtr);
                    return hr;
                }
                finally
                {
                    WinRT.WindowsDeleteString(hAppId);
                }
            }
            finally
            {
                Marshal.Release(managerPtr);
            }
        }
        finally
        {
            WinRT.WindowsDeleteString(hClassName);
        }
    }

    private void ResetNativeState()
    {
        _runtimeAvailable = false;
        if (_notifierPtr != 0)
        {
            Marshal.Release(_notifierPtr);
            _notifierPtr = 0;
        }
    }

    private static void TrySetCurrentProcessAumid(string appId)
    {
        try
        {
            // Best-effort. Failure is non-fatal — Windows can still resolve the
            // AUMID via the Start-Menu shortcut.
            _ = Shell32.SetCurrentProcessExplicitAppUserModelID(appId);
        }
        catch
        {
            // shell32.dll missing on Nano Server etc. — ignore.
        }
    }

    public NotificationHandle Show(NotificationContent content)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureRuntimeAvailable();

        // Build toast XML
        string xml = BuildToastXml(content);

        // Create XmlDocument and load the XML
        nint xmlDocPtr = CreateXmlDocument(xml);

        try
        {
            // Create ToastNotification from XmlDocument
            nint toastPtr = CreateToastNotification(xmlDocPtr);

            try
            {
                uint id = ++_nextId;
                var handle = new NotificationHandle
                {
                    NativeHandle = toastPtr,
                    Tag = content.Tag,
                    Group = content.Group,
                    PlatformId = id
                };
                _activeNotifications[id] = handle;

                // Set Tag/Group on the toast if provided
                if (!string.IsNullOrEmpty(content.Tag))
                    SetToastTag(toastPtr, content.Tag);
                if (!string.IsNullOrEmpty(content.Group))
                    SetToastGroup(toastPtr, content.Group);

                // Show the toast via IToastNotifier::Show (vtable slot 6)
                int hr = WinRT.IToastNotifier_Show(_notifierPtr, toastPtr);
                Marshal.ThrowExceptionForHR(hr);

                return handle;
            }
            catch
            {
                Marshal.Release(toastPtr);
                throw;
            }
        }
        finally
        {
            Marshal.Release(xmlDocPtr);
        }
    }

    public void Hide(NotificationHandle handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_runtimeAvailable || _notifierPtr == 0 || handle.NativeHandle == 0) return;

        // IToastNotifier::Hide (vtable slot 7)
        WinRT.IToastNotifier_Hide(_notifierPtr, handle.NativeHandle);
        _activeNotifications.Remove(handle.PlatformId);
    }

    public void ClearAll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_runtimeAvailable) return;

        nint hClassName = WinRT.CreateHString("Windows.UI.Notifications.ToastNotificationManager");
        try
        {
            var historyIid = WinRT.IID_IToastNotificationManagerStatics2;
            int hr = WinRT.RoGetActivationFactory(hClassName, ref historyIid, out nint manager2Ptr);
            if (hr < 0) return;

            try
            {
                hr = WinRT.IToastNotificationManagerStatics2_GetHistory(manager2Ptr, out nint historyPtr);
                if (hr >= 0 && historyPtr != 0)
                {
                    nint hAppId = WinRT.CreateHString(_appId);
                    try
                    {
                        WinRT.IToastNotificationHistory_Clear(historyPtr, hAppId);
                    }
                    finally
                    {
                        WinRT.WindowsDeleteString(hAppId);
                        Marshal.Release(historyPtr);
                    }
                }
            }
            finally
            {
                Marshal.Release(manager2Ptr);
            }
        }
        finally
        {
            WinRT.WindowsDeleteString(hClassName);
        }

        _activeNotifications.Clear();
    }

    public void Remove(string tag, string? group = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_runtimeAvailable) return;

        nint hClassName = WinRT.CreateHString("Windows.UI.Notifications.ToastNotificationManager");
        try
        {
            var historyIid = WinRT.IID_IToastNotificationManagerStatics2;
            int hr = WinRT.RoGetActivationFactory(hClassName, ref historyIid, out nint manager2Ptr);
            if (hr < 0) return;

            try
            {
                hr = WinRT.IToastNotificationManagerStatics2_GetHistory(manager2Ptr, out nint historyPtr);
                if (hr >= 0 && historyPtr != 0)
                {
                    try
                    {
                        nint hTag = WinRT.CreateHString(tag);
                        nint hGroup = group != null ? WinRT.CreateHString(group) : 0;
                        nint hAppId = WinRT.CreateHString(_appId);

                        try
                        {
                            if (hGroup != 0)
                                WinRT.IToastNotificationHistory_RemoveGroupedTagWithId(historyPtr, hTag, hGroup, hAppId);
                            else
                                WinRT.IToastNotificationHistory_RemoveTagWithId(historyPtr, hTag, hAppId);
                        }
                        finally
                        {
                            WinRT.WindowsDeleteString(hTag);
                            if (hGroup != 0) WinRT.WindowsDeleteString(hGroup);
                            WinRT.WindowsDeleteString(hAppId);
                        }
                    }
                    finally
                    {
                        Marshal.Release(historyPtr);
                    }
                }
            }
            finally
            {
                Marshal.Release(manager2Ptr);
            }
        }
        finally
        {
            WinRT.WindowsDeleteString(hClassName);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var kv in _activeNotifications)
        {
            if (kv.Value.NativeHandle != 0)
                Marshal.Release(kv.Value.NativeHandle);
        }
        _activeNotifications.Clear();

        if (_notifierPtr != 0)
        {
            Marshal.Release(_notifierPtr);
            _notifierPtr = 0;
        }
        _runtimeAvailable = false;
    }

    private void EnsureRuntimeAvailable()
    {
        if (_runtimeAvailable && _notifierPtr != 0)
            return;

        if (!OsVersionSupported)
            throw new PlatformNotSupportedException(
                "Windows toast notifications require Windows 10 (10.0.10240) or later.");

        if (_initializationFailure != null)
            throw new PlatformNotSupportedException(
                "Windows notification platform is unavailable on this system.",
                _initializationFailure);

        throw new InvalidOperationException(
            "WindowsNotificationBackend has not been initialized. Call Initialize first.");
    }

    #endregion

    #region Toast XML Builder

    private static string BuildToastXml(NotificationContent content)
    {
        var sb = new StringBuilder(512);
        sb.Append("<toast");
        if (content.Arguments.Count > 0)
        {
            sb.Append(" launch=\"");
            bool first = true;
            foreach (var kv in content.Arguments)
            {
                if (!first) sb.Append('&');
                sb.Append(Escape(kv.Key)).Append('=').Append(Escape(kv.Value));
                first = false;
            }
            sb.Append('"');
        }
        sb.Append('>');

        // Visual
        sb.Append("<visual><binding template=\"ToastGeneric\">");
        sb.Append("<text>").Append(Escape(content.Title)).Append("</text>");
        if (!string.IsNullOrEmpty(content.Body))
            sb.Append("<text>").Append(Escape(content.Body)).Append("</text>");
        var iconPath = NotificationImageHelper.ResolveToPath(content.Icon);
        if (!string.IsNullOrEmpty(iconPath))
            sb.Append("<image placement=\"appLogoOverride\" src=\"").Append(Escape(iconPath)).Append("\"/>");
        var imagePath = NotificationImageHelper.ResolveToPath(content.Image);
        if (!string.IsNullOrEmpty(imagePath))
            sb.Append("<image placement=\"hero\" src=\"").Append(Escape(imagePath)).Append("\"/>");
        sb.Append("</binding></visual>");

        // Actions
        if (content.Actions.Count > 0)
        {
            sb.Append("<actions>");
            foreach (var action in content.Actions)
            {
                sb.Append("<action content=\"").Append(Escape(action.Label ?? string.Empty)).Append('"');
                sb.Append(" arguments=\"actionId=").Append(Escape(action.Id ?? string.Empty));
                if (action.Arguments != null)
                {
                    foreach (var kv in action.Arguments)
                        sb.Append('&').Append(Escape(kv.Key)).Append('=').Append(Escape(kv.Value));
                }
                sb.Append("\"/>");
            }
            sb.Append("</actions>");
        }

        // Audio
        if (content.Silent)
            sb.Append("<audio silent=\"true\"/>");

        sb.Append("</toast>");
        return sb.ToString();
    }

    private static string Escape(string s)
    {
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("\"", "&quot;").Replace("'", "&apos;");
    }

    #endregion

    #region COM Factory Helpers

    private static nint CreateXmlDocument(string xml)
    {
        // Activate Windows.Data.Xml.Dom.XmlDocument
        nint hClassName = WinRT.CreateHString("Windows.Data.Xml.Dom.XmlDocument");
        int hr;
        nint xmlDocInspectable;
        try
        {
            hr = WinRT.RoActivateInstance(hClassName, out xmlDocInspectable);
        }
        finally
        {
            WinRT.WindowsDeleteString(hClassName);
        }
        Marshal.ThrowExceptionForHR(hr);

        // QI for IXmlDocumentIO to call LoadXml
        var xmlDocIoIid = WinRT.IID_IXmlDocumentIO;
        hr = Marshal.QueryInterface(xmlDocInspectable, in xmlDocIoIid, out nint xmlDocIoPtr);
        Marshal.ThrowExceptionForHR(hr);

        try
        {
            nint hXml = WinRT.CreateHString(xml);
            try
            {
                hr = WinRT.IXmlDocumentIO_LoadXml(xmlDocIoPtr, hXml);
                Marshal.ThrowExceptionForHR(hr);
            }
            finally
            {
                WinRT.WindowsDeleteString(hXml);
            }
        }
        finally
        {
            Marshal.Release(xmlDocIoPtr);
        }

        return xmlDocInspectable;
    }

    private static nint CreateToastNotification(nint xmlDocPtr)
    {
        // Get IToastNotificationFactory via RoGetActivationFactory
        var factoryIid = WinRT.IID_IToastNotificationFactory;
        nint hClassName = WinRT.CreateHString("Windows.UI.Notifications.ToastNotification");
        int hr;
        nint factoryPtr;
        try
        {
            hr = WinRT.RoGetActivationFactory(hClassName, ref factoryIid, out factoryPtr);
        }
        finally
        {
            WinRT.WindowsDeleteString(hClassName);
        }
        Marshal.ThrowExceptionForHR(hr);

        try
        {
            // IToastNotificationFactory::CreateToastNotification(XmlDocument) – vtable slot 6
            hr = WinRT.IToastNotificationFactory_CreateToastNotification(factoryPtr, xmlDocPtr, out nint toastPtr);
            Marshal.ThrowExceptionForHR(hr);
            return toastPtr;
        }
        finally
        {
            Marshal.Release(factoryPtr);
        }
    }

    private static void SetToastTag(nint toastPtr, string tag)
    {
        // QI for IToastNotification2 to set Tag
        var iid = WinRT.IID_IToastNotification2;
        int hr = Marshal.QueryInterface(toastPtr, in iid, out nint toast2Ptr);
        if (hr < 0) return;

        try
        {
            nint hTag = WinRT.CreateHString(tag);
            try
            {
                WinRT.IToastNotification2_PutTag(toast2Ptr, hTag);
            }
            finally
            {
                WinRT.WindowsDeleteString(hTag);
            }
        }
        finally
        {
            Marshal.Release(toast2Ptr);
        }
    }

    private static void SetToastGroup(nint toastPtr, string group)
    {
        var iid = WinRT.IID_IToastNotification2;
        int hr = Marshal.QueryInterface(toastPtr, in iid, out nint toast2Ptr);
        if (hr < 0) return;

        try
        {
            nint hGroup = WinRT.CreateHString(group);
            try
            {
                WinRT.IToastNotification2_PutGroup(toast2Ptr, hGroup);
            }
            finally
            {
                WinRT.WindowsDeleteString(hGroup);
            }
        }
        finally
        {
            Marshal.Release(toast2Ptr);
        }
    }

    #endregion

    #region Shortcut (non-packaged app AUMID registration)

    private static void EnsureShortcut(string appId, string appName, Guid activatorClsid)
    {
        // Non-packaged (Win32) apps need a Start-Menu shortcut whose property
        // store carries BOTH PKEY_AppUserModel_ID and (Win10 RS5+)
        // PKEY_AppUserModel_ToastActivatorCLSID. Without the activator CLSID,
        // CreateToastNotifierWithId returns WPN_E_PLATFORM_UNAVAILABLE.
        // We always refresh the shortcut so a previous build that wrote it
        // without the CLSID can be repaired in place.
        try
        {
            string shortcutPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs",
                $"{appName}.lnk");

            if (File.Exists(shortcutPath) && ShortcutHasMatchingMetadata(shortcutPath, appId, activatorClsid))
                return;

            string? dir = Path.GetDirectoryName(shortcutPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var shellLinkClsid = new Guid("00021401-0000-0000-C000-000000000046");
            var shellLinkIid = new Guid("000214F9-0000-0000-C000-000000000046");

            int hr = Ole32.CoCreateInstance(ref shellLinkClsid, 0, 1 /* CLSCTX_INPROC_SERVER */,
                ref shellLinkIid, out nint shellLinkPtr);
            if (hr < 0) return;

            try
            {
                var vtbl = *(nint**)shellLinkPtr;

                // IShellLinkW::SetPath — slot 20.
                string exePath = Environment.ProcessPath ?? string.Empty;
                nint hPath = Marshal.StringToHGlobalUni(exePath);
                try
                {
                    ((delegate* unmanaged[Stdcall]<nint, nint, int>)vtbl[20])(shellLinkPtr, hPath);
                }
                finally
                {
                    Marshal.FreeHGlobal(hPath);
                }

                var propStoreIid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
                hr = Marshal.QueryInterface(shellLinkPtr, in propStoreIid, out nint propStorePtr);
                if (hr >= 0)
                {
                    try
                    {
                        SetAppUserModelId(propStorePtr, appId);
                        SetToastActivatorClsid(propStorePtr, activatorClsid);
                        CommitPropertyStore(propStorePtr);
                    }
                    finally
                    {
                        Marshal.Release(propStorePtr);
                    }
                }

                var persistFileIid = new Guid("0000010b-0000-0000-C000-000000000046");
                hr = Marshal.QueryInterface(shellLinkPtr, in persistFileIid, out nint persistFilePtr);
                if (hr >= 0)
                {
                    try
                    {
                        // IPersistFile::Save — slot 6 (IUnknown 3 + IPersist 1 + reserved 2).
                        var pvtbl = *(nint**)persistFilePtr;
                        nint hFile = Marshal.StringToHGlobalUni(shortcutPath);
                        try
                        {
                            ((delegate* unmanaged[Stdcall]<nint, nint, int, int>)pvtbl[6])(persistFilePtr, hFile, 1);
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(hFile);
                        }
                    }
                    finally
                    {
                        Marshal.Release(persistFilePtr);
                    }
                }
            }
            finally
            {
                Marshal.Release(shellLinkPtr);
            }
        }
        catch
        {
            // Best-effort — if the shortcut can't be written we fall through and
            // CreateToastNotifierWithId will fail with WPN_E_PLATFORM_UNAVAILABLE,
            // which the caller already handles as a soft failure.
        }
    }

    /// <summary>
    /// Reads back the AUMID and ToastActivatorCLSID from an existing shortcut.
    /// Returns true when both match what we would write, so the existing
    /// shortcut can be reused without a rewrite.
    /// </summary>
    private static bool ShortcutHasMatchingMetadata(string shortcutPath, string appId, Guid activatorClsid)
    {
        try
        {
            var shellLinkClsid = new Guid("00021401-0000-0000-C000-000000000046");
            var shellLinkIid = new Guid("000214F9-0000-0000-C000-000000000046");

            int hr = Ole32.CoCreateInstance(ref shellLinkClsid, 0, 1, ref shellLinkIid, out nint shellLinkPtr);
            if (hr < 0) return false;

            try
            {
                var persistFileIid = new Guid("0000010b-0000-0000-C000-000000000046");
                hr = Marshal.QueryInterface(shellLinkPtr, in persistFileIid, out nint persistFilePtr);
                if (hr < 0) return false;

                try
                {
                    var pvtbl = *(nint**)persistFilePtr;
                    nint hFile = Marshal.StringToHGlobalUni(shortcutPath);
                    try
                    {
                        // IPersistFile::Load — slot 5. dwMode = STGM_READ (0).
                        hr = ((delegate* unmanaged[Stdcall]<nint, nint, int, int>)pvtbl[5])(persistFilePtr, hFile, 0);
                        if (hr < 0) return false;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(hFile);
                    }
                }
                finally
                {
                    Marshal.Release(persistFilePtr);
                }

                var propStoreIid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
                hr = Marshal.QueryInterface(shellLinkPtr, in propStoreIid, out nint propStorePtr);
                if (hr < 0) return false;

                try
                {
                    string? existingAumid = ReadAppUserModelId(propStorePtr);
                    Guid? existingClsid = ReadToastActivatorClsid(propStorePtr);
                    return string.Equals(existingAumid, appId, StringComparison.Ordinal)
                        && existingClsid == activatorClsid;
                }
                finally
                {
                    Marshal.Release(propStorePtr);
                }
            }
            finally
            {
                Marshal.Release(shellLinkPtr);
            }
        }
        catch
        {
            return false;
        }
    }

    private static readonly Guid PKEY_AppUserModelId_Fmtid = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    private static void SetAppUserModelId(nint propStorePtr, string appId)
    {
        // PKEY_AppUserModel_ID = {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, 5
        var pkey = new PropertyKey { fmtid = PKEY_AppUserModelId_Fmtid, pid = 5 };

        var propVar = new PropVariant
        {
            vt = 31, // VT_LPWSTR
            payload = Marshal.StringToCoTaskMemUni(appId)
        };

        try
        {
            var vtbl = *(nint**)propStorePtr;
            ((delegate* unmanaged[Stdcall]<nint, PropertyKey*, PropVariant*, int>)vtbl[6])(
                propStorePtr, &pkey, &propVar);
        }
        finally
        {
            if (propVar.payload != 0)
                Marshal.FreeCoTaskMem(propVar.payload);
        }
    }

    private static void SetToastActivatorClsid(nint propStorePtr, Guid clsid)
    {
        // PKEY_AppUserModel_ToastActivatorCLSID = {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, 26
        var pkey = new PropertyKey { fmtid = PKEY_AppUserModelId_Fmtid, pid = 26 };

        // VT_CLSID (72) — payload is a pointer to a 16-byte GUID.
        nint pclsid = Marshal.AllocCoTaskMem(16);
        try
        {
            byte[] bytes = clsid.ToByteArray();
            Marshal.Copy(bytes, 0, pclsid, 16);

            var propVar = new PropVariant
            {
                vt = 72, // VT_CLSID
                payload = pclsid
            };

            var vtbl = *(nint**)propStorePtr;
            ((delegate* unmanaged[Stdcall]<nint, PropertyKey*, PropVariant*, int>)vtbl[6])(
                propStorePtr, &pkey, &propVar);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pclsid);
        }
    }

    private static void CommitPropertyStore(nint propStorePtr)
    {
        var vtbl = *(nint**)propStorePtr;
        // IPropertyStore::Commit — slot 7.
        ((delegate* unmanaged[Stdcall]<nint, int>)vtbl[7])(propStorePtr);
    }

    private static string? ReadAppUserModelId(nint propStorePtr)
    {
        var pkey = new PropertyKey { fmtid = PKEY_AppUserModelId_Fmtid, pid = 5 };
        var propVar = default(PropVariant);
        var vtbl = *(nint**)propStorePtr;

        // IPropertyStore::GetValue — slot 5.
        int hr = ((delegate* unmanaged[Stdcall]<nint, PropertyKey*, PropVariant*, int>)vtbl[5])(
            propStorePtr, &pkey, &propVar);
        try
        {
            if (hr < 0) return null;
            if (propVar.vt != 31 /* VT_LPWSTR */) return null;
            if (propVar.payload == 0) return null;
            return Marshal.PtrToStringUni(propVar.payload);
        }
        finally
        {
            PropVariantClear(&propVar);
        }
    }

    private static Guid? ReadToastActivatorClsid(nint propStorePtr)
    {
        var pkey = new PropertyKey { fmtid = PKEY_AppUserModelId_Fmtid, pid = 26 };
        var propVar = default(PropVariant);
        var vtbl = *(nint**)propStorePtr;

        int hr = ((delegate* unmanaged[Stdcall]<nint, PropertyKey*, PropVariant*, int>)vtbl[5])(
            propStorePtr, &pkey, &propVar);
        try
        {
            if (hr < 0) return null;
            if (propVar.vt != 72 /* VT_CLSID */) return null;
            if (propVar.payload == 0) return null;

            byte[] buf = new byte[16];
            Marshal.Copy(propVar.payload, buf, 0, 16);
            return new Guid(buf);
        }
        finally
        {
            PropVariantClear(&propVar);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(PropVariant* pvar);

    /// <summary>
    /// Stable per-AUMID activator CLSID. It does NOT need to be a registered
    /// COM server (we never receive callbacks); Windows only inspects the
    /// property to validate that the toast originates from a "modern" app.
    /// </summary>
    private static Guid DeriveActivatorClsid(string appId)
    {
        Span<byte> hash = stackalloc byte[20];
        System.Security.Cryptography.SHA1.HashData(
            Encoding.UTF8.GetBytes("Jalium.UI.ToastActivator:" + appId), hash);
        Span<byte> guidBytes = stackalloc byte[16];
        hash[..16].CopyTo(guidBytes);
        // RFC 4122 §4.3 — UUID v5 layout.
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
    }

    /// <summary>
    /// PROPVARIANT layout sized to match the native struct (24 bytes on x64,
    /// 16 on x86). We only consume <see cref="payload"/>, but the trailing
    /// <see cref="payloadHigh"/> field is required so SetValue / GetValue
    /// don't write past our struct.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public nint payload;
        public nint payloadHigh;
    }

    #endregion

    #region COM Activator Registration (HKCU)

    /// <summary>
    /// Registers the per-AUMID toast activator CLSID under
    /// <c>HKCU\Software\Classes\CLSID\{clsid}</c> with a <c>LocalServer32</c>
    /// pointing at the running exe.
    /// <para>
    /// The Windows Push Notification user-mode service (<c>wpnapps.dll</c>)
    /// validates that the ToastActivatorCLSID set on the .lnk resolves to a
    /// real COM server before issuing a notifier — otherwise it fails the
    /// internal lookup with <c>0x80040154 (REGDB_E_CLASSNOTREG)</c> and
    /// surfaces it as <c>0x803E0105 (WPN_E_PLATFORM_UNAVAILABLE)</c>.
    /// We never hook the activation callback ourselves; the registry entry
    /// only has to <em>exist</em> for the validation to pass.
    /// </para>
    /// </summary>
    private static void EnsureToastActivatorRegistered(Guid clsid, string appName)
    {
        try
        {
            string clsidStr = "{" + clsid.ToString().ToUpperInvariant() + "}";
            string clsidKeyPath = $@"Software\Classes\CLSID\{clsidStr}";
            string exePath = Environment.ProcessPath ?? string.Empty;
            string serverCommand = "\"" + exePath + "\"";

            if (Advapi32.TryCreateRegKey(Advapi32.HKEY_CURRENT_USER, clsidKeyPath, out nint clsidKey))
            {
                try
                {
                    Advapi32.TrySetStringValue(clsidKey, null, $"{appName} Toast Activator");
                }
                finally
                {
                    Advapi32.RegCloseKey(clsidKey);
                }
            }

            string localServerPath = clsidKeyPath + @"\LocalServer32";
            if (Advapi32.TryCreateRegKey(Advapi32.HKEY_CURRENT_USER, localServerPath, out nint serverKey))
            {
                try
                {
                    Advapi32.TrySetStringValue(serverKey, null, serverCommand);
                }
                finally
                {
                    Advapi32.RegCloseKey(serverKey);
                }
            }
        }
        catch
        {
            // Best-effort. Failure surfaces later as WPN_E_PLATFORM_UNAVAILABLE
            // which the caller already translates into a soft state.
        }
    }

    #endregion
}

#region WinRT COM Interop Helpers

/// <summary>
/// Raw WinRT COM vtable call helpers for toast notification APIs.
/// All methods are thin wrappers around unmanaged function-pointer calls.
/// </summary>
internal static unsafe class WinRT
{
    // ── WinRT Initialization ─────────────────────────────────────────
    private static bool s_initialized;

    [DllImport("api-ms-win-core-winrt-l1-1-0.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int RoInitialize(int initType);

    /// <summary>
    /// Ensures the WinRT runtime is initialized on the current thread.
    /// Throws on hard failures.
    /// </summary>
    public static void EnsureInitialized()
    {
        int hr = TryEnsureInitialized();
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
    }

    /// <summary>
    /// HRESULT-returning variant of <see cref="EnsureInitialized"/>. Used by
    /// the toast backend's hot init path so callers can decide whether to
    /// translate a failure into a soft "platform unavailable" state instead
    /// of throwing.
    /// </summary>
    public static int TryEnsureInitialized()
    {
        if (s_initialized) return 0;

        int hr;
        try
        {
            // RO_INIT_SINGLETHREADED = 0, RO_INIT_MULTITHREADED = 1
            hr = RoInitialize(0);
            if (hr < 0)
            {
                hr = RoInitialize(1);
                // RPC_E_CHANGED_MODE — already initialized differently — is fine.
                if (hr == unchecked((int)0x80010106)) hr = 0;
            }
        }
        catch (DllNotFoundException)
        {
            // api-ms-win-core-winrt shim missing (Nano Server, etc.)
            return unchecked((int)0x80040154); // REGDB_E_CLASSNOTREG
        }

        if (hr >= 0) s_initialized = true;
        return hr;
    }

    // ── IIDs ──────────────────────────────────────────────────────────
    // IToastNotificationManagerStatics  {50AC103F-D235-4598-BBEF-98FE4D1A3AD4}
    public static Guid IID_IToastNotificationManagerStatics =
        new("50AC103F-D235-4598-BBEF-98FE4D1A3AD4");

    // IToastNotificationManagerStatics2 {7AB93C52-0E48-4750-BA9D-1A4113981847}
    public static Guid IID_IToastNotificationManagerStatics2 =
        new("7AB93C52-0E48-4750-BA9D-1A4113981847");

    // IToastNotificationFactory         {04124B20-82C6-4229-B109-FD9ED4662B53}
    public static Guid IID_IToastNotificationFactory =
        new("04124B20-82C6-4229-B109-FD9ED4662B53");

    // IToastNotification2               {9DFB9FD1-143A-490E-90BF-B9FBA7132DE7}
    public static Guid IID_IToastNotification2 =
        new("9DFB9FD1-143A-490E-90BF-B9FBA7132DE7");

    // IToastNotificationHistory         {5caddc63-01d3-4c97-986f-0533483fee14}
    public static Guid IID_IToastNotificationHistory =
        new("5CADDC63-01D3-4C97-986F-0533483FEE14");

    // IXmlDocument                      {F7F3A506-1E87-42D6-BCFB-B8C809FA5494}
    public static Guid IID_IXmlDocument =
        new("F7F3A506-1E87-42D6-BCFB-B8C809FA5494");

    // IXmlDocumentIO                    {6CD0E74E-EE65-4489-9EBF-CA43E87BA637}
    public static Guid IID_IXmlDocumentIO =
        new("6CD0E74E-EE65-4489-9EBF-CA43E87BA637");

    // ── WindowsCreateString / WindowsDeleteString ─────────────────────
    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out nint hstring);

    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int WindowsDeleteString(nint hstring);

    public static nint CreateHString(string s)
    {
        int hr = TryCreateHString(s, out nint h);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        return h;
    }

    /// <summary>
    /// HRESULT-returning variant of <see cref="CreateHString"/> used by paths
    /// that translate runtime failures into a soft state instead of throwing.
    /// </summary>
    public static int TryCreateHString(string s, out nint h)
    {
        try
        {
            return WindowsCreateString(s, s.Length, out h);
        }
        catch (DllNotFoundException)
        {
            h = 0;
            return unchecked((int)0x80040154); // REGDB_E_CLASSNOTREG
        }
    }

    // ── RoGetActivationFactory / RoActivateInstance ──────────────────
    [DllImport("api-ms-win-core-winrt-l1-1-0.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int RoGetActivationFactory(
        nint activatableClassId, ref Guid iid, out nint factory);

    [DllImport("api-ms-win-core-winrt-l1-1-0.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int RoActivateInstance(nint activatableClassId, out nint instance);

    // ── Vtable call wrappers ────────────────────────────────────────

    // IToastNotificationManagerStatics::CreateToastNotifierWithId
    // IUnknown(3) + IInspectable(3) = 6 base slots
    // CreateToastNotifier() = slot 6, CreateToastNotifierWithId(HSTRING) = slot 7
    public static int IToastNotificationManagerStatics_CreateToastNotifierWithId(
        nint @this, nint hAppId, out nint notifierPtr)
    {
        var vtbl = *(nint**)@this;
        nint result = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)vtbl[7])(
            @this, hAppId, &result);
        notifierPtr = result;
        return hr;
    }

    // IToastNotificationManagerStatics2::get_History – slot 6
    public static int IToastNotificationManagerStatics2_GetHistory(
        nint @this, out nint historyPtr)
    {
        var vtbl = *(nint**)@this;
        nint result = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)vtbl[6])(@this, &result);
        historyPtr = result;
        return hr;
    }

    // IToastNotifier::Show – IUnknown(3)+IInspectable(3)+Show=slot 6
    public static int IToastNotifier_Show(nint @this, nint toastNotification)
    {
        var vtbl = *(nint**)@this;
        return ((delegate* unmanaged[Stdcall]<nint, nint, int>)vtbl[6])(@this, toastNotification);
    }

    // IToastNotifier::Hide – slot 7
    public static int IToastNotifier_Hide(nint @this, nint toastNotification)
    {
        var vtbl = *(nint**)@this;
        return ((delegate* unmanaged[Stdcall]<nint, nint, int>)vtbl[7])(@this, toastNotification);
    }

    // IToastNotificationFactory::CreateToastNotification(XmlDocument) – slot 6
    public static int IToastNotificationFactory_CreateToastNotification(
        nint @this, nint content, out nint toastNotification)
    {
        var vtbl = *(nint**)@this;
        nint result = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)vtbl[6])(
            @this, content, &result);
        toastNotification = result;
        return hr;
    }

    // IToastNotification2::put_Tag – slot 8 (IUnknown3+IInspectable3+get_Tag=6+put_Tag=7 →
    //   wait, IToastNotification2 extends IInspectable: 3+3=6 base, get_Tag=6, put_Tag=7, get_Group=8, put_Group=9)
    public static int IToastNotification2_PutTag(nint @this, nint hTag)
    {
        var vtbl = *(nint**)@this;
        return ((delegate* unmanaged[Stdcall]<nint, nint, int>)vtbl[7])(@this, hTag);
    }

    public static int IToastNotification2_PutGroup(nint @this, nint hGroup)
    {
        var vtbl = *(nint**)@this;
        return ((delegate* unmanaged[Stdcall]<nint, nint, int>)vtbl[9])(@this, hGroup);
    }

    // IXmlDocumentIO::LoadXml – slot 6
    public static int IXmlDocumentIO_LoadXml(nint @this, nint hXml)
    {
        var vtbl = *(nint**)@this;
        return ((delegate* unmanaged[Stdcall]<nint, nint, int>)vtbl[6])(@this, hXml);
    }

    // IToastNotificationHistory::Clear(appId) – slot 10
    // Slots: IUnknown(3)+IInspectable(3) = 6 base
    // RemoveGroup=6, RemoveGroupWithId=7, Remove(tag,group,appId)=8, Remove(tag)=9, Clear=10
    public static int IToastNotificationHistory_Clear(nint @this, nint hAppId)
    {
        var vtbl = *(nint**)@this;
        return ((delegate* unmanaged[Stdcall]<nint, nint, int>)vtbl[10])(@this, hAppId);
    }

    // IToastNotificationHistory::Remove(tag, group, appId) – slot 8
    public static int IToastNotificationHistory_RemoveGroupedTagWithId(
        nint @this, nint hTag, nint hGroup, nint hAppId)
    {
        var vtbl = *(nint**)@this;
        return ((delegate* unmanaged[Stdcall]<nint, nint, nint, nint, int>)vtbl[8])(
            @this, hTag, hGroup, hAppId);
    }

    // Simplified remove by tag (uses RemoveGroupedTagWithId with empty group as fallback)
    public static int IToastNotificationHistory_RemoveTagWithId(
        nint @this, nint hTag, nint hAppId)
    {
        // Use slot 7: Remove(tag, appId) – not all Windows versions have this,
        // fall back to RemoveGroupedTagWithId with empty group
        var vtbl = *(nint**)@this;
        nint hEmpty = CreateHString(string.Empty);
        try
        {
            return ((delegate* unmanaged[Stdcall]<nint, nint, nint, nint, int>)vtbl[8])(
                @this, hTag, hEmpty, hAppId);
        }
        finally
        {
            WindowsDeleteString(hEmpty);
        }
    }
}

/// <summary>
/// Ole32 COM helpers.
/// </summary>
internal static class Ole32
{
    [DllImport("ole32.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int CoCreateInstance(
        ref Guid rclsid, nint pUnkOuter, uint dwClsContext,
        ref Guid riid, out nint ppv);
}

/// <summary>
/// Shell32 helpers for AUMID registration on the running process.
/// </summary>
internal static class Shell32
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = true)]
    public static extern int SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string AppID);
}

/// <summary>
/// Minimal advapi32 wrapper for HKCU registry writes (toast activator CLSID).
/// </summary>
internal static class Advapi32
{
    public const nint HKEY_CURRENT_USER = unchecked((nint)(int)0x80000001);
    private const int KEY_WRITE = 0x20006;
    private const int REG_SZ = 1;
    private const int REG_OPTION_NON_VOLATILE = 0;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegCreateKeyExW")]
    private static extern int RegCreateKeyEx(
        nint hKey,
        [MarshalAs(UnmanagedType.LPWStr)] string lpSubKey,
        int Reserved,
        [MarshalAs(UnmanagedType.LPWStr)] string? lpClass,
        int dwOptions,
        int samDesired,
        nint lpSecurityAttributes,
        out nint phkResult,
        out int lpdwDisposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegSetValueExW")]
    private static extern int RegSetValueEx(
        nint hKey,
        [MarshalAs(UnmanagedType.LPWStr)] string? lpValueName,
        int Reserved,
        int dwType,
        [MarshalAs(UnmanagedType.LPWStr)] string lpData,
        int cbData);

    [DllImport("advapi32.dll")]
    public static extern int RegCloseKey(nint hKey);

    public static bool TryCreateRegKey(nint root, string subKeyPath, out nint key)
    {
        int hr = RegCreateKeyEx(
            root, subKeyPath, 0, null,
            REG_OPTION_NON_VOLATILE, KEY_WRITE,
            0, out key, out _);
        return hr == 0 && key != 0;
    }

    public static void TrySetStringValue(nint key, string? valueName, string data)
    {
        // cbData is in bytes and must include the trailing null character.
        int cbData = (data.Length + 1) * sizeof(char);
        _ = RegSetValueEx(key, valueName, 0, REG_SZ, data, cbData);
    }
}

#endregion
