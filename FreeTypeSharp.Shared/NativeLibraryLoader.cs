using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using System.Reflection;

namespace FreeTypeSharp
{
    public static class NativeLibraryLoader
    {
        const int RTLD_NOW = 2;

        public static string NativeLibraryPath { get; private set; }
        public delegate IntPtr SymbolLookupDelegate(IntPtr addr, string name);

        private static IntPtr _freetypeAddr;
        private static SymbolLookupDelegate _symbolLookup;

#if !__IOS__ && !WINDOWS_UAP
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpFileName);
#endif

#if WINDOWS_UAP
        [DllImport("kernel32", CallingConvention = CallingConvention.Cdecl, EntryPoint = "LoadPackagedLibrary")]
        private static extern IntPtr LoadLibrary([MarshalAs(UnmanagedType.LPWStr)] string libFileName, int reserved = 0);
#endif

#if !__IOS__ || WINDOWS_UAP
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procname);
#endif

#if !__IOS__ && !WINDOWS_UAP
        [DllImport("libdl")]
        private static extern IntPtr dlopen(string fileName, int flags);
#endif

#if !__IOS__  && !WINDOWS_UAP
        [DllImport("libdl")]
        private static extern IntPtr dlerror();
#endif

#if !__IOS__  && !WINDOWS_UAP
        [DllImport("libdl")]
        private static extern IntPtr dlsym(IntPtr handle, string symbol);
#endif

        static NativeLibraryLoader()
        {

#if NETSTANDARD2_0
            // Figure out which OS we're on. Windows or "other".
            if (Environment.OSVersion.Platform == PlatformID.Unix ||
                        Environment.OSVersion.Platform == PlatformID.MacOSX ||
                        // Legacy mono value. See https://www.mono-project.com/docs/faq/technical/
                        (int)Environment.OSVersion.Platform == 128)
            {
                _freetypeAddr = LoadPosixLibrary(out _symbolLookup);
            }
            else
            {
                _freetypeAddr = LoadWindowsLibrary(out _symbolLookup);
            }
#elif WINDOWS_UAP
            _freetypeAddr = LoadWindowsUniversalLibrary(out _symbolLookup);
#elif __IOS__
            _freetypeAddr = LoadiOSLibrary(out _symbolLookup);
#elif ANDROID
            _freetypeAddr = LoadAndroidLibrary(out _symbolLookup);
#endif
        }

        public static T LoadFunction<T>(string function, bool throwIfNotFound = false) where T : class
        {
#if __IOS__
            var methodInfo = typeof(Native.FT_DllImport).GetMethod(function, BindingFlags.Static | BindingFlags.Public);
            if (methodInfo == null)
            {
                if (throwIfNotFound)
                    throw new EntryPointNotFoundException(function);

                return default(T);
            }

            return methodInfo.CreateDelegate(typeof(T)) as T;
#else
            var ret = _symbolLookup(_freetypeAddr, function);
            if (ret == IntPtr.Zero)
            {
                if (throwIfNotFound)
                {
#if WINDOWS_UAP
                    throw new Exception(string.Format("function {0} not found!", function));
#else
                    throw new EntryPointNotFoundException(function);
#endif
                }

                return default(T);
            }

            return Marshal.GetDelegateForFunctionPointer<T>(ret);
#endif
        }

#if NETSTANDARD2_0
        private static IntPtr LoadWindowsLibrary(out SymbolLookupDelegate symbolLookup)
        {
            string libFile = "freetype.dll";
            string arch = Environment.Is64BitProcess ? "win-x64" : "win-x86";
            string rootDirectory = AppDomain.CurrentDomain.BaseDirectory;

            var paths = new[]
            {
                // This is where native libraries in our nupkg should end up
                Path.Combine(rootDirectory, "runtimes", arch, "native", libFile),
                Path.Combine(rootDirectory, Environment.Is64BitProcess ? "x64" : "x86", libFile),
                Path.Combine(rootDirectory, libFile)
            };

            foreach (var path in paths)
            {
                if (path == null) continue;
                if (!File.Exists(path)) continue;

                var addr = LoadLibrary(path);
                if (addr == IntPtr.Zero)
                    throw new Exception("LoadLibrary failed: " + path);

                symbolLookup = GetProcAddress;
                NativeLibraryPath = path;
                return addr;
            }

            throw new Exception("LoadLibrary failed: unable to locate library " + libFile + ". Searched: " + paths.Aggregate((a, b) => a + "; " + b));
        }
#endif

#if WINDOWS_UAP
        private static IntPtr LoadWindowsUniversalLibrary(out SymbolLookupDelegate symbolLookup)
        {
            var addr = LoadLibrary("freetype.dll");

            if (addr == IntPtr.Zero)
                throw new Exception("LoadPackagedLibrary failed: freetype.dll");

            symbolLookup = GetProcAddress;
            NativeLibraryPath = ".";
            return addr;
        }
#endif

#if ANDROID
        private static IntPtr LoadAndroidLibrary(out SymbolLookupDelegate symbolLookup)
        {
            var libName = "libfreetype.so";
            var addr = dlopen(libName, RTLD_NOW);

            if (addr == IntPtr.Zero)
            {
                // Not using NanosmgException because it depends on nn_errno.
                var error = Marshal.PtrToStringAnsi(dlerror());
                throw new Exception("Android - dlopen failed: " + libName + " : " + error);
            }
            symbolLookup = dlsym;
            NativeLibraryPath = libName;
            return addr;
        }
#endif

        private static IntPtr LoadiOSLibrary(out SymbolLookupDelegate symbolLookup)
        {
            symbolLookup = null;
            NativeLibraryPath = "__Internal";
            return IntPtr.Zero;
        }

#if NETSTANDARD2_0
        private static IntPtr LoadPosixLibrary(out SymbolLookupDelegate symbolLookup)
        {
            string rootDirectory = AppDomain.CurrentDomain.BaseDirectory;

            // Environment.OSVersion.Platform returns "Unix" for Unix or OSX, so use RuntimeInformation here
            var isOsx = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
            string libFile = isOsx ? "libfreetype.dylib" : "libfreetype.so";
            string arch = isOsx ? "osx" : "linux-" + (Environment.Is64BitProcess ? "x64" : "x86");

            // Search a few different locations for our native assembly
            var paths = new[]
            {
                // This is where native libraries in our nupkg should end up
                Path.Combine(rootDirectory, "runtimes", arch, "native", libFile),
                // The build output folder
                Path.Combine(rootDirectory, libFile),
                Path.Combine("/usr/local/lib", libFile),
                Path.Combine("/usr/lib", libFile)
            };

            foreach (var path in paths)
            {
                if (path == null) continue;
                if (!File.Exists(path)) continue;

                var addr = dlopen(path, RTLD_NOW);
                if (addr == IntPtr.Zero)
                {
                    // Not using NanosmgException because it depends on nn_errno.
                    var error = Marshal.PtrToStringAnsi(dlerror());
                    throw new Exception("dlopen failed: " + path + " : " + error);
                }

                symbolLookup = dlsym;
                NativeLibraryPath = path;
                return addr;
            }

            throw new Exception("dlopen failed: unable to locate library " + libFile + ". Searched: " + paths.Aggregate((a, b) => a + "; " + b));
        }
#endif

    }
}
