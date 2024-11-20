﻿using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Biohazrd;
using Biohazrd.CSharp;
using Biohazrd.OutputGeneration;
using Biohazrd.Transformation.Common;
using Biohazrd.Utilities;
using Astra.ImGui.Generator;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("    Astra.ImGui.Generator <path-to-dear-imgui-source> <path-to-astra-dearimgui-native> <path-to-output>");
    return 1;
}

const string canonical_build_variant = "Release";
string dotNetRid;
string nativeRuntimeBuildScript;
string importLibraryName;
bool itaniumExportMode;

if (OperatingSystem.IsWindows())
{
    dotNetRid = "win-x64";
    nativeRuntimeBuildScript = "build-native.cmd";
    importLibraryName = "Astra.ImGui.Native.lib";
    itaniumExportMode = false;
}
else if (OperatingSystem.IsLinux())
{
    dotNetRid = "linux-x64";
    nativeRuntimeBuildScript = "build-native.sh";
    importLibraryName = "libAstra.ImGui.Native.so";
    itaniumExportMode = true;
}
// TODO: Add MacOS support
else
{
    Console.Error.WriteLine($"'{RuntimeInformation.OSDescription}' is not supported by this generator.");
    return 1;
}

string sourceDirectoryPath = Path.GetFullPath(args[0]);
string mainHeaderFilePath = Path.Combine(sourceDirectoryPath, "imgui.h");

string imGuiBackendsDirectoryPath = Path.Combine(sourceDirectoryPath, "backends");
string dearImGuiNativeRootPath = Path.GetFullPath(args[1]);
string imGuiLibFilePath = Path.Combine(dearImGuiNativeRootPath, "..", "bin", "Astra.ImGui.Native", dotNetRid, canonical_build_variant, importLibraryName);
string imGuiInlineExporterFilePath = Path.Combine(dearImGuiNativeRootPath, "InlineExportHelper.gen.cpp");
string nativeBuildScript = Path.Combine(dearImGuiNativeRootPath, nativeRuntimeBuildScript);

string outputDirectoryPath = Path.GetFullPath(args[2]);

if (!Directory.Exists(sourceDirectoryPath))
{
    Console.Error.WriteLine($"Dear ImGui directory '{sourceDirectoryPath}' not found.");
    return 1;
}

if (!File.Exists(mainHeaderFilePath))
{
    Console.Error.WriteLine($"Dear ImGui header file not found at '{mainHeaderFilePath}'.");
    return 1;
}

string imGuiConfigFilePath = Path.Combine(dearImGuiNativeRootPath, "DearImGuiConfig.h");

if (!File.Exists(imGuiConfigFilePath))
{
    Console.Error.WriteLine($"Could not find Dear ImGui config file '{imGuiConfigFilePath}'.");
    return 1;
}

string[] backendFiles =
    {
        "imgui_impl_win32.h",
        "imgui_impl_win32.cpp",
        "imgui_impl_dx11.h",
        "imgui_impl_dx11.cpp"
    };

// Copy backend files to sourceDirectory temporarily
foreach (string file in backendFiles)
{
    string path = Path.Combine(imGuiBackendsDirectoryPath, file);
    if (File.Exists(path) == false) continue;
    File.Copy(path, Path.Combine(sourceDirectoryPath, file), true);
}

// Create the library
TranslatedLibraryBuilder libraryBuilder = new()
{
    Options = new TranslationOptions()
    {
        // The only template that appears on the public API is ImVector<T>, which we special-case as a C# generic.
        // ImPool<T>, ImChunkStream<T>, and ImSpan<T> do appear on the internal API but for now we just want them to be dropped.
        //TODO: In theory this could be made to work, but there's a few wrinkles that need to be ironed out and these few API points are not a high priority.
        EnableTemplateSupport = false,
    }
};
libraryBuilder.AddCommandLineArgument("--language=c++");
libraryBuilder.AddCommandLineArgument($"-I{sourceDirectoryPath}");
libraryBuilder.AddCommandLineArgument($"-DIMGUI_USER_CONFIG=\"{imGuiConfigFilePath}\"");
libraryBuilder.AddFile(mainHeaderFilePath);
libraryBuilder.AddFile(Path.Combine(sourceDirectoryPath, "imgui_internal.h"));

// Include backend header files
foreach (string file in backendFiles)
{
    string path = Path.Combine(sourceDirectoryPath, file);
    if (file.Contains("loader")) continue;
    if (File.Exists(path) == false || file.Split('.').Last() != "h") continue;
    Console.WriteLine("Adding backend header file: " + file);
    libraryBuilder.AddFile(path);
}

TranslatedLibrary library = libraryBuilder.Create();
TranslatedLibraryConstantEvaluator constantEvaluator = libraryBuilder.CreateConstantEvaluator();

// Start output session
using OutputSession outputSession = new()
{
    AutoRenameConflictingFiles = true,
    BaseOutputDirectory = outputDirectoryPath,
    ConservativeFileLogging = false
};

// Apply transformations
Console.WriteLine("==============================================================================");
Console.WriteLine("Performing library-specific transformations...");
Console.WriteLine("==============================================================================");

library = new RemoveUnneededDeclarationsTransformation().Transform(library);
library = new ImGuiEnumTransformation().Transform(library);

BrokenDeclarationExtractor brokenDeclarationExtractor = new();
library = brokenDeclarationExtractor.Transform(library);

library = new RemoveExplicitBitFieldPaddingFieldsTransformation().Transform(library);
library = new AddBaseVTableAliasTransformation().Transform(library);
library = new ConstOverloadRenameTransformation().Transform(library);
library = new MakeEverythingPublicTransformation().Transform(library);
library = new ImGuiCSharpTypeReductionTransformation().Transform(library);
library = new MiscFixesTransformation().Transform(library);
library = new LiftAnonymousRecordFieldsTransformation().Transform(library);
library = new AddTrampolineMethodOptionsTransformation(MethodImplOptions.AggressiveInlining).Transform(library);
library = new ImGuiInternalFixupTransformation().Transform(library);
library = new AstraImGuiNamespaceTransformation().Transform(library);
library = new RemoveIllegalImVectorReferencesTransformation().Transform(library);
library = new MoveLooseDeclarationsIntoTypesTransformation
(
    (_, d) =>
    {
        if (d.Namespace == "Astra.ImGui")
        {
            return "ImGui";
        }
        if (d.Namespace == "Astra.ImGui.Internal")
        {
            return "ImGuiInternal";
        }
        if (d.Namespace == "Astra.ImGui.Backends.Direct3D11")
        {
            return "ImGuiImplD3D11";
        }
        if (d.Namespace == "Astra.ImGui.Backends.Win32")
        {
            return "ImGuiImplWin32";
        }
        return "Globals";
    }
).Transform(library);
library = new AutoNameUnnamedParametersTransformation().Transform(library);
library = new CreateTrampolinesTransformation
{
    TargetRuntime = TargetRuntime.Net8
}.Transform(library);
library = new ImGuiCreateStringWrappersTransformation().Transform(library);
library = new StripUnreferencedLazyDeclarationsTransformation().Transform(library);
library = new ImGuiKeyIssueWorkaroundTransformation().Transform(library);
library = new DeduplicateNamesTransformation().Transform(library);
library = new OrganizeOutputFilesByNamespaceTransformation("Astra.ImGui").Transform(library); // Relies on AstraImGuiNamespaceTransformation, MoveLooseDeclarationsIntoTypesTransformation
library = new ImVersionConstantsTransformation(library, constantEvaluator).Transform(library);
library = new VectorTypeTransformation().Transform(library);

// Generate the inline export helper
library = new InlineExportHelper(outputSession, imGuiInlineExporterFilePath) { __ItaniumExportMode = itaniumExportMode }.Transform(library);

// Rebuild the native DLL so that the librarian can access a version of the library including the inline-exported functions
Console.WriteLine("Rebuilding Astra.ImGui.Native...");
Process.Start(new ProcessStartInfo(nativeBuildScript)
{
    WorkingDirectory = dearImGuiNativeRootPath
})!.WaitForExit();

// Use librarian to identifiy DLL exports
LinkImportsTransformation linkImports = new()
{
    ErrorOnMissing = true,
    TrackVerboseImportInformation = true,
    WarnOnAmbiguousSymbols = true
};
linkImports.AddLibrary(imGuiLibFilePath);
library = linkImports.Transform(library);

// Perform validation
Console.WriteLine("==============================================================================");
Console.WriteLine("Performing post-translation validation...");
Console.WriteLine("==============================================================================");

library = new CSharpTranslationVerifier().Transform(library);

// Remove final broken declarations
library = brokenDeclarationExtractor.Transform(library);

// Emit the translation
Console.WriteLine("==============================================================================");
Console.WriteLine("Emitting translation...");
Console.WriteLine("==============================================================================");
ImmutableArray<TranslationDiagnostic> generationDiagnostics = CSharpLibraryGenerator.Generate
(
    CSharpGenerationOptions.Default with
    {
        TargetRuntime = TargetRuntime.Net6,
        InfrastructureTypesNamespace = "Astra.ImGui.Infrastructure",
    },
    outputSession,
    library
);

// Write out diagnostics log
DiagnosticWriter diagnostics = new();
diagnostics.AddFrom(library);
diagnostics.AddFrom(brokenDeclarationExtractor);
diagnostics.AddCategory("Generation Diagnostics", generationDiagnostics, "Generation completed successfully");

using StreamWriter diagnosticsOutput = outputSession.Open<StreamWriter>("Diagnostics.log");
diagnostics.WriteOutDiagnostics(diagnosticsOutput, writeToConsole: true);

// Remove copied backend files
foreach (string file in backendFiles)
{
    string path = Path.Combine(sourceDirectoryPath, file);
    if (File.Exists(path) == false) continue;
    File.Delete(path);
}

return 0;