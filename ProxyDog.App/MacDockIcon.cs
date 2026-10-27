using System.Runtime.InteropServices;

namespace ProxyDog.UI;

/// <summary>
/// Avalonia's Window.Icon does not touch the Dock, so when run outside an .app bundle (dotnet run) the Dock shows a
/// generic icon. This sets the application icon through AppKit; any failure just leaves the default.
/// </summary>
static class MacDockIcon
{
    const string ObjC = "/usr/lib/libobjc.dylib";

    [DllImport(ObjC)] static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr bytes, nuint length);

    public static void Set(Stream png)
    {
        if (!OperatingSystem.IsMacOS()) return;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            using var ms = new MemoryStream();
            png.CopyTo(ms);
            var bytes = ms.ToArray();
            buffer = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, buffer, bytes.Length);

            var data = Send(objc_getClass("NSData"), sel_registerName("dataWithBytes:length:"), buffer, (nuint)bytes.Length);
            var image = Send(Send(objc_getClass("NSImage"), sel_registerName("alloc")), sel_registerName("initWithData:"), data);
            if (image == IntPtr.Zero) return;
            var app = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            Send(app, sel_registerName("setApplicationIconImage:"), image);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            // Cosmetic only.
        }
        finally
        {
            // NSData copied the bytes, so the buffer can go.
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }
}

/// <summary>
/// Menu-bar icons on macOS are "template" images: black shapes on transparent that the system tints for light, dark
/// and wallpaper-coloured bars. Avalonia hands the tray image to AppKit without that flag, so set it on the status
/// item's button ourselves.
/// </summary>
static class MacStatusItem
{
    const string ObjC = "/usr/lib/libobjc.dylib";

    [DllImport(ObjC)] static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC)] static extern IntPtr object_getClassName(IntPtr obj);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);

    static IntPtr Sel(string name) => sel_registerName(name);
    static bool Bool(IntPtr value) => ((long)value & 0xFF) != 0;
    static string ClassName(IntPtr obj) => Marshal.PtrToStringAnsi(object_getClassName(obj)) ?? "";

    /// <summary>Returns how many status-bar images were marked as template (0 if none were found yet).</summary>
    public static int MarkTemplate()
    {
        if (!OperatingSystem.IsMacOS()) return 0;
        try
        {
            var app = Send(objc_getClass("NSApplication"), Sel("sharedApplication"));
            var windows = Send(app, Sel("windows"));
            var count = (int)Send(windows, Sel("count"));
            var marked = 0;
            for (var i = 0; i < count; i++)
            {
                var window = Send(windows, Sel("objectAtIndex:"), (IntPtr)i);
                if (!ClassName(window).Contains("StatusBar")) continue;
                marked += MarkViews(Send(window, Sel("contentView")));
            }
            return marked;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CGRect { public double X, Y, W, H; }

    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern CGRect SendRect(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr SendRectArg(IntPtr receiver, IntPtr selector, CGRect rect);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern void SendRectRep(IntPtr receiver, IntPtr selector, CGRect rect, IntPtr rep);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern double SendDouble(IntPtr receiver, IntPtr selector);

    /// <summary>
    /// Debug aid: renders the status-bar button into a bitmap and reports the colour of the glyph pixels, so the
    /// tinting can be checked without screen-recording permission. Arm64 only (struct returns differ on x64).
    /// </summary>
    public static string DescribeRendering()
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64) return "n/a";
        var app = Send(objc_getClass("NSApplication"), Sel("sharedApplication"));
        var windows = Send(app, Sel("windows"));
        var count = (int)Send(windows, Sel("count"));
        for (var i = 0; i < count; i++)
        {
            var window = Send(windows, Sel("objectAtIndex:"), (IntPtr)i);
            if (!ClassName(window).Contains("StatusBar")) continue;
            var button = FindButton(Send(window, Sel("contentView")));
            if (button == IntPtr.Zero) continue;

            var rect = SendRect(button, Sel("bounds"));
            var rep = SendRectArg(button, Sel("bitmapImageRepForCachingDisplayInRect:"), rect);
            SendRectRep(button, Sel("cacheDisplayInRect:toBitmapImageRep:"), rect, rep);
            var data = Send(rep, Sel("bitmapData"));
            var w = (int)Send(rep, Sel("pixelsWide"));
            var h = (int)Send(rep, Sel("pixelsHigh"));
            var stride = (int)Send(rep, Sel("bytesPerRow"));
            var spp = (int)Send(rep, Sel("samplesPerPixel"));
            long solid = 0, r = 0, g = 0, b = 0;
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var o = y * stride + x * spp;
                    var a = spp == 4 ? Marshal.ReadByte(data, o + 3) : 255;
                    if (a < 128) continue;
                    solid++;
                    r += Marshal.ReadByte(data, o); g += Marshal.ReadByte(data, o + 1); b += Marshal.ReadByte(data, o + 2);
                }
            var template = Bool(Send(Send(button, Sel("image")), Sel("isTemplate")));
            return solid == 0
                ? $"button {w}x{h}, template={template}: nothing drawn (fully transparent)"
                : $"button {w}x{h}, template={template}: {solid} opaque pixels, average colour rgb({r / solid},{g / solid},{b / solid})";
        }
        return "no status-bar button found";
    }

    static IntPtr FindButton(IntPtr view)
    {
        if (view == IntPtr.Zero) return IntPtr.Zero;
        if (Bool(Send(view, Sel("isKindOfClass:"), objc_getClass("NSButton")))) return view;
        var subviews = Send(view, Sel("subviews"));
        var n = (int)Send(subviews, Sel("count"));
        for (var i = 0; i < n; i++)
        {
            var found = FindButton(Send(subviews, Sel("objectAtIndex:"), (IntPtr)i));
            if (found != IntPtr.Zero) return found;
        }
        return IntPtr.Zero;
    }

    static string Str(IntPtr nsString) => nsString == IntPtr.Zero ? "(null)" : Marshal.PtrToStringUTF8(Send(nsString, Sel("UTF8String"))) ?? "(null)";

    /// <summary>The name macOS uses for the running app (menu bar, Cmd-Tab, Activity Monitor).</summary>
    public static string AppNames()
    {
        if (!OperatingSystem.IsMacOS()) return "n/a";
        var info = Send(objc_getClass("NSProcessInfo"), Sel("processInfo"));
        var running = Send(objc_getClass("NSRunningApplication"), Sel("currentApplication"));
        return $"processName='{Str(Send(info, Sel("processName")))}' localizedName='{Str(Send(running, Sel("localizedName")))}'";
    }

    /// <summary>
    /// Avalonia names the app "Avalonia Application" when it starts, before our Application object exists, so the
    /// menu bar shows that name. Setting the process name afterwards is what AppKit reads for the app menu title.
    /// </summary>
    public static void SetAppName(string name)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var info = Send(objc_getClass("NSProcessInfo"), Sel("processInfo"));
        var ns = Send(Send(objc_getClass("NSString"), Sel("alloc")), Sel("initWithUTF8String:"), Marshal.StringToCoTaskMemUTF8(name));
        Send(info, Sel("setProcessName:"), ns);
    }

    /// <summary>Avalonia hard-codes "About Avalonia" and a bare "Quit" in the app menu; give them the app name. Safe to call repeatedly.</summary>
    public static void RenameAboutItem(string appName)
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            var app = Send(objc_getClass("NSApplication"), Sel("sharedApplication"));
            var menu = Send(app, Sel("mainMenu"));
            if (menu == IntPtr.Zero) return;
            var items = Send(menu, Sel("itemArray"));
            if ((int)Send(items, Sel("count")) == 0) return;
            var appMenu = Send(Send(items, Sel("objectAtIndex:"), IntPtr.Zero), Sel("submenu"));
            if (appMenu == IntPtr.Zero) return;
            var subItems = Send(appMenu, Sel("itemArray"));
            var n = (int)Send(subItems, Sel("count"));
            for (var i = 0; i < n; i++)
            {
                var item = Send(subItems, Sel("objectAtIndex:"), (IntPtr)i);
                var current = Str(Send(item, Sel("title")));
                var wanted = current.StartsWith("About Avalonia", StringComparison.Ordinal) ? $"About {appName}"
                           : current == "Quit" ? $"Quit {appName}"
                           : null;
                if (wanted is null) continue;
                var title = Send(Send(objc_getClass("NSString"), Sel("alloc")), Sel("initWithUTF8String:"), Marshal.StringToCoTaskMemUTF8(wanted));
                Send(item, Sel("setTitle:"), title);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            // Cosmetic only.
        }
    }

    /// <summary>Debug aid: the title macOS shows for the app in the menu bar, and the titles of its first menu items.</summary>
    public static string DescribeMainMenu()
    {
        if (!OperatingSystem.IsMacOS()) return "n/a";
        var app = Send(objc_getClass("NSApplication"), Sel("sharedApplication"));
        var menu = Send(app, Sel("mainMenu"));
        if (menu == IntPtr.Zero) return "no main menu";
        var items = Send(menu, Sel("itemArray"));
        var n = (int)Send(items, Sel("count"));
        var titles = new List<string>();
        for (var i = 0; i < Math.Min(n, 3); i++)
        {
            var item = Send(items, Sel("objectAtIndex:"), (IntPtr)i);
            var sub = Send(item, Sel("submenu"));
            var title = Marshal.PtrToStringUTF8(Send(Send(sub, Sel("title")), Sel("UTF8String"))) ?? "";
            var first = "";
            if (i == 0)
            {
                var subItems = Send(sub, Sel("itemArray"));
                var k = (int)Send(subItems, Sel("count"));
                var names = new List<string>();
                for (var j = 0; j < k; j++)
                    names.Add(Str(Send(Send(subItems, Sel("objectAtIndex:"), (IntPtr)j), Sel("title"))));
                first = " [items: " + string.Join(" | ", names) + "]";
            }
            titles.Add($"'{title}'{first}");
        }
        return string.Join(", ", titles);
    }

    static int MarkViews(IntPtr view)
    {
        if (view == IntPtr.Zero) return 0;
        var marked = 0;
        if (Bool(Send(view, Sel("isKindOfClass:"), objc_getClass("NSButton"))))
        {
            var image = Send(view, Sel("image"));
            if (image != IntPtr.Zero)
            {
                Send(image, Sel("setTemplate:"), (IntPtr)1);
                if (Bool(Send(image, Sel("isTemplate")))) marked++;
            }
        }
        var subviews = Send(view, Sel("subviews"));
        var n = (int)Send(subviews, Sel("count"));
        for (var i = 0; i < n; i++)
            marked += MarkViews(Send(subviews, Sel("objectAtIndex:"), (IntPtr)i));
        return marked;
    }
}
