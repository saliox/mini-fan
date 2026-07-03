using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("Mini Fan")]
[assembly: AssemblyDescription("Controle automatique du Cooler Boost sur portable MSI")]
[assembly: AssemblyProduct("Mini Fan")]
[assembly: AssemblyCompany("Hasu")]
[assembly: AssemblyCopyright("Hasu 2026")]
[assembly: ComVisible(false)]
[assembly: AssemblyVersion(MiniFan.AppVersion.Number)]
[assembly: AssemblyFileVersion(MiniFan.AppVersion.Number)]

namespace MiniFan
{
    public static class AppVersion
    {
        public const string Number = "1.0.0";
    }
}
