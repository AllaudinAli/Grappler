using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;

[assembly: AssemblyTitle("Grappler")]
[assembly: AssemblyDescription("Desktop downloader built on yt-dlp")]
[assembly: AssemblyProduct("Grappler")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyCopyright("Copyright (c) 2026 AllaudinAIi - MIT License")]
[assembly: ComVisible(false)]

[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

// Note: MSBuild generates the [TargetFramework(".NETFramework,v4.8")]
// attribute itself, which is what stops the runtime applying pre-4.6
// quirks - including the one that would refuse to rescale WPF content on
// a different-DPI monitor. Declaring it here as well is a build error.

// Resource lookups never need to fall back to a satellite assembly.
[assembly: ThemeInfo(ResourceDictionaryLocation.None, ResourceDictionaryLocation.SourceAssembly)]
