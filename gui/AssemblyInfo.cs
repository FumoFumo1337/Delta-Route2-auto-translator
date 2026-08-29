using System.Reflection;
using System.Resources;
using System.Runtime.InteropServices;

// Compiled into both executables. csc turns these attributes into the Win32
// version resource, which the binaries previously did not have at all: an
// unsigned .NET file with a blank version block is one of the features the
// machine-learning scanners weigh most heavily, and it costs nothing to state
// truthfully what this is.
[assembly: AssemblyCompany("Delta translation tools")]
[assembly: AssemblyProduct("Delta/Route2 translation toolset")]
[assembly: AssemblyCopyright("MIT license")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
[assembly: NeutralResourcesLanguage("en")]
[assembly: ComVisible(false)]

[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]
