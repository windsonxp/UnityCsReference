// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor.Build.Reporting;
using UnityEditor.Modules;
using UnityEditor.Scripting;
using UnityEditor.Scripting.Compilers;
using UnityEngine;
using UnityEditor;
using UnityEditor.Utils;
using Debug = UnityEngine.Debug;

namespace UnityEditorInternal
{
    internal class AssemblyStripper
    {
        private static bool debugUnstripped
        {
            get
            {
                return false;
            }
        }

        private static string[] Il2CppBlacklistPaths
        {
            get
            {
                return new[]
                {
                    Path.Combine("..", "platform_native_link.xml")
                };
            }
        }

        private static string UnityLinkerPath
        {
            get
            {
                return Path.Combine(IL2CPPUtils.GetIl2CppFolder(), "build/UnityLinker.exe");
            }
        }

        private static string GetModuleWhitelist(string module, string moduleStrippingInformationFolder)
        {
            return Paths.Combine(moduleStrippingInformationFolder, module + ".xml");
        }

        private static bool StripAssembliesTo(string[] assemblies, string[] searchDirs, string outputFolder, string workingDirectory, out string output, out string error, string linkerPath, IIl2CppPlatformProvider platformProvider, IEnumerable<string> additionalBlacklist, BuildTargetGroup buildTargetGroup, ManagedStrippingLevel managedStrippingLevel, bool stripEngineCode, string editorToLinkerDataPath)
        {
            if (!Directory.Exists(outputFolder))
                Directory.CreateDirectory(outputFolder);

            additionalBlacklist = additionalBlacklist.Select(s => Path.IsPathRooted(s) ? s : Path.Combine(workingDirectory, s)).Where(File.Exists);

            var userBlackLists = GetUserBlacklistFiles();

            foreach (var ub in userBlackLists)
                Console.WriteLine("UserBlackList: " + ub);

            additionalBlacklist = additionalBlacklist.Concat(userBlackLists);

            var args = new List<string>
            {
                $"-out={CommandLineFormatter.PrepareFileName(outputFolder)}",
            };

            if (!UseUnityLinkerEngineModuleStripping)
            {
                args.Add($"-x={CommandLineFormatter.PrepareFileName(GetModuleWhitelist("Core", platformProvider.moduleStrippingInformationFolder))}");
            }

            args.AddRange(additionalBlacklist.Select(path => $"-x={CommandLineFormatter.PrepareFileName(path)}"));

            args.AddRange(searchDirs.Select(d => $"-d={CommandLineFormatter.PrepareFileName(d)}"));
            args.AddRange(assemblies.Select(assembly => $"--include-unity-root-assembly={CommandLineFormatter.PrepareFileName(Path.GetFullPath(assembly))}"));
            args.Add($"--dotnetruntime={GetRuntimeArgumentValueForLinker(buildTargetGroup)}");
            args.Add($"--dotnetprofile={GetProfileArgumentValueForLinker(buildTargetGroup)}");
            args.Add("--use-editor-options");
            args.Add($"--include-directory={CommandLineFormatter.PrepareFileName(workingDirectory)}");

            if (EditorUserBuildSettings.allowDebugging)
                args.Add("--editor-settings-flag=AllowDebugging");

            if (EditorUserBuildSettings.development)
                args.Add("--editor-settings-flag=Development");

            args.Add($"--rule-set={GetRuleSetForStrippingLevel(managedStrippingLevel)}");
            args.Add($"--editor-data-file={CommandLineFormatter.PrepareFileName(editorToLinkerDataPath)}");

            var compilerPlatform = "";
            var compilerArchitecture = "";
            Il2CppNativeCodeBuilder il2cppNativeCodeBuilder = platformProvider.CreateIl2CppNativeCodeBuilder();
            if (il2cppNativeCodeBuilder != null)
            {
                compilerPlatform = il2cppNativeCodeBuilder.CompilerPlatform;
                compilerArchitecture = il2cppNativeCodeBuilder.CompilerArchitecture;
            }
            else
            {
                // When the scripting backend is not IL2CPP, we have to map those strings and use a utility function to figure out proper strings.
                GetUnityLinkerPlatformStringsFromBuildTarget(platformProvider.target, out compilerPlatform, out compilerArchitecture);
            }

            args.Add($"--platform={compilerPlatform}");
            if (!string.IsNullOrEmpty(compilerArchitecture))
                args.Add($"--architecture={compilerArchitecture}");

            if (!UseUnityLinkerEngineModuleStripping)
            {
                args.Add("--disable-engine-module-support");
            }

            if (stripEngineCode)
            {
                args.Add("--enable-engine-module-stripping");

                if (UnityEngine.Connect.UnityConnectSettings.enabled)
                    args.Add("--engine-stripping-flag=EnableUnityConnect");

                if (UnityEngine.Analytics.PerformanceReporting.enabled)
                    args.Add("--engine-stripping-flag=EnablePerformanceReporting");

                if (UnityEngine.Analytics.Analytics.enabled)
                    args.Add("--engine-stripping-flag=EnableAnalytics");

                if (UnityEditor.CrashReporting.CrashReportingSettings.enabled)
                    args.Add("--engine-stripping-flag=EnableCrashReporting");

                if (UnityEditorInternal.VR.VRModule.ShouldInjectVRDependenciesForBuildTarget(platformProvider.target))
                    args.Add("--engine-stripping-flag=EnableVR");
            }

            var modulesAssetPath = Path.Combine(platformProvider.moduleStrippingInformationFolder, "../modules.asset");
            if (File.Exists(modulesAssetPath))
                args.Add($"--engine-modules-asset-file={CommandLineFormatter.PrepareFileName(modulesAssetPath)}");

            var additionalArgs = System.Environment.GetEnvironmentVariable("UNITYLINKER_ADDITIONAL_ARGS");
            if (!string.IsNullOrEmpty(additionalArgs))
                args.Add(additionalArgs);

            additionalArgs = Debug.GetDiagnosticSwitch("VMUnityLinkerAdditionalArgs") as string;
            if (!string.IsNullOrEmpty(additionalArgs))
                args.Add(additionalArgs.Trim('\''));

            return RunAssemblyLinker(args, out output, out error, linkerPath, workingDirectory);
        }

        private static string GetRuleSetForStrippingLevel(ManagedStrippingLevel managedStrippingLevel)
        {
            switch (managedStrippingLevel)
            {
                case ManagedStrippingLevel.Low:
                    return "Conservative";
                case ManagedStrippingLevel.Medium:
                    return "Aggressive";
                case ManagedStrippingLevel.High:
                    return "Experimental";
            }

            throw new ArgumentException($"Unhandled {nameof(ManagedStrippingLevel)} value of {managedStrippingLevel}");
        }

        private static void GetUnityLinkerPlatformStringsFromBuildTarget(BuildTarget target, out string platform, out string architecture)
        {
            switch (target)
            {
                case BuildTarget.StandaloneWindows64:
                    platform = "WindowsDesktop";
                    architecture = "x64";
                    break;
                case BuildTarget.StandaloneWindows:
                    platform = "WindowsDesktop";
                    architecture = "x86";
                    break;
                case BuildTarget.Android:
                    // Do not supply architecture for Android.
                    // The build pipeline bundles multiple architectures for Android.
                    // Can't narrow down to a specific architecture at strip time, we work around
                    // that fact in the UnityLinker.
                    platform = "Android";
                    architecture = "";
                    break;
                case BuildTarget.StandaloneLinux64:
                    platform = "Linux";
                    architecture = "x64";
                    break;
                case BuildTarget.StandaloneOSX:
                    platform = "MacOSX";
                    architecture = "x64";
                    break;
                case BuildTarget.WSAPlayer:
                    platform = "WinRT";
                    // Could be multiple values.  We don't have use of this information yet so don't bother with trying to figure out what it should be
                    architecture = "";
                    break;
                case BuildTarget.iOS:
                    platform = "iOS";
                    architecture = "ARM64";
                    break;
                case BuildTarget.tvOS:
                    platform = "tvOS";
                    architecture = "ARM64";
                    break;
                default:
                    throw new ArgumentException($"Mapping to UnityLinker platform not implemented for {nameof(BuildTarget)} `{target}`");
            }
        }

        private static bool RunAssemblyLinker(IEnumerable<string> args, out string @out, out string err, string linkerPath, string workingDirectory)
        {
            var argString = args.Aggregate((buff, s) => buff + " " + s);
            Console.WriteLine("Invoking UnityLinker with arguments: " + argString);
            Runner.RunManagedProgram(linkerPath, argString, workingDirectory, null, null);

            @out = "";
            err = "";

            return true;
        }

        private static List<string> GetUserAssemblies(RuntimeClassRegistry rcr, string managedDir)
        {
            return rcr.GetUserAssemblies().Where(s => rcr.IsDLLUsed(s)).Select(s => Path.Combine(managedDir, s)).ToList();
        }

        internal static void StripAssemblies(string managedAssemblyFolderPath, IIl2CppPlatformProvider platformProvider, RuntimeClassRegistry rcr, ManagedStrippingLevel managedStrippingLevel)
        {
            var assemblies = GetUserAssemblies(rcr, managedAssemblyFolderPath);
            assemblies.AddRange(Directory.GetFiles(managedAssemblyFolderPath, "I18N*.dll", SearchOption.TopDirectoryOnly));
            var assembliesToStrip = assemblies.ToArray();

            var searchDirs = new[]
            {
                managedAssemblyFolderPath
            };

            RunAssemblyStripper(assemblies, managedAssemblyFolderPath, assembliesToStrip, searchDirs, UnityLinkerPath, platformProvider, rcr, managedStrippingLevel);
        }

        internal static void GenerateInternalCallSummaryFile(string icallSummaryPath, string managedAssemblyFolderPath, string strippedDLLPath)
        {
            var exe = Path.Combine(MonoInstallationFinder.GetFrameWorksFolder(), "Tools/InternalCallRegistrationWriter/InternalCallRegistrationWriter.exe");
            var dlls = Directory.GetFiles(strippedDLLPath, "UnityEngine.*Module.dll").Concat(new[] {Path.Combine(strippedDLLPath, "UnityEngine.dll")});
            var args = string.Format("-output=\"{0}\" -summary=\"{1}\" -assembly=\"{2}\"",
                Path.Combine(managedAssemblyFolderPath, "UnityICallRegistration.cpp"),
                icallSummaryPath,
                dlls.Aggregate((dllArg, next) => dllArg + ";" + next)
            );
            Runner.RunManagedProgram(exe, args);
        }

        internal static IEnumerable<string> GetUserBlacklistFiles()
        {
            return Directory.GetFiles("Assets", "link.xml", SearchOption.AllDirectories).Select(s => Path.Combine(Directory.GetCurrentDirectory(), s));
        }

        private static bool AddWhiteListsForModules(IEnumerable<string> nativeModules, ref IEnumerable<string> blacklists, string moduleStrippingInformationFolder)
        {
            bool result = false;
            foreach (var module in nativeModules)
            {
                var moduleWhitelist = GetModuleWhitelist(module, moduleStrippingInformationFolder);

                if (File.Exists(moduleWhitelist))
                {
                    if (!blacklists.Contains(moduleWhitelist))
                    {
                        blacklists = blacklists.Concat(new[] { moduleWhitelist });
                        result = true;
                    }
                }
            }
            return result;
        }

        private static string GetRuntimeArgumentValueForLinker(BuildTargetGroup buildTargetGroup)
        {
            var backend = PlayerSettings.GetScriptingBackend(buildTargetGroup);
            switch (backend)
            {
                case ScriptingImplementation.IL2CPP:
                    return "il2cpp";
                case ScriptingImplementation.Mono2x:
                    return "mono";
                default:
                    throw new NotImplementedException($"Don't know the backend value to pass to UnityLinker for {backend}");
            }
        }

        private static string GetProfileArgumentValueForLinker(BuildTargetGroup buildTargetGroup)
        {
            return IL2CPPUtils.ApiCompatibilityLevelToDotNetProfileArgument(PlayerSettings.GetApiCompatibilityLevel(buildTargetGroup));
        }

        private static void RunAssemblyStripper(IEnumerable assemblies, string managedAssemblyFolderPath, string[] assembliesToStrip, string[] searchDirs, string monoLinkerPath, IIl2CppPlatformProvider platformProvider, RuntimeClassRegistry rcr, ManagedStrippingLevel managedStrippingLevel)
        {
            string output;
            string error;
            var buildTargetGroup = BuildPipeline.GetBuildTargetGroup(platformProvider.target);
            bool isMono = PlayerSettings.GetScriptingBackend(buildTargetGroup) == ScriptingImplementation.Mono2x;
            bool engineStrippingSupported = platformProvider.supportsEngineStripping && !isMono;
            bool performEngineStripping = rcr != null && PlayerSettings.stripEngineCode && engineStrippingSupported;
            IEnumerable<string> blacklists = Il2CppBlacklistPaths;

            if (rcr != null)
            {
                blacklists = blacklists.Concat(new[]
                {
                    WriteMethodsToPreserveBlackList(rcr, platformProvider.target),
                    MonoAssemblyStripping.GenerateLinkXmlToPreserveDerivedTypes(managedAssemblyFolderPath, rcr),
                    WriteTypesInScenesBlacklist(managedAssemblyFolderPath, rcr)
                });
            }

            if (isMono)
            {
                // The old Mono assembly stripper uses per-platform link.xml files if available. Apply these here.
                var buildToolsDirectory = BuildPipeline.GetBuildToolsDirectory(platformProvider.target);
                if (!string.IsNullOrEmpty(buildToolsDirectory))
                {
                    var platformDescriptor = Path.Combine(buildToolsDirectory, "link.xml");
                    if (File.Exists(platformDescriptor))
                        blacklists = blacklists.Concat(new[] {platformDescriptor});
                }
            }

            string editorToLinkerDataPath = WriteEditorData(managedAssemblyFolderPath, rcr);

            if (!performEngineStripping && !UseUnityLinkerEngineModuleStripping)
            {
                // if we don't do stripping, add all modules blacklists.
                foreach (var file in Directory.GetFiles(platformProvider.moduleStrippingInformationFolder, "*.xml"))
                    blacklists = blacklists.Concat(new[] {file});
            }

            // Generated link xml files that would have been empty will be nulled out.  Need to filter these out before running the linker
            blacklists = blacklists.Where(b => b != null);

            var tempStripPath = Path.GetFullPath(Path.Combine(managedAssemblyFolderPath, "tempStrip"));

            bool addedMoreBlacklists;
            do
            {
                addedMoreBlacklists = false;

                if (EditorUtility.DisplayCancelableProgressBar("Building Player", "Stripping assemblies", 0.0f))
                    throw new OperationCanceledException();

                if (!StripAssembliesTo(
                    assembliesToStrip,
                    searchDirs,
                    tempStripPath,
                    managedAssemblyFolderPath,
                    out output,
                    out error,
                    monoLinkerPath,
                    platformProvider,
                    blacklists,
                    buildTargetGroup,
                    managedStrippingLevel,
                    performEngineStripping,
                    editorToLinkerDataPath))
                    throw new Exception("Error in stripping assemblies: " + assemblies + ", " + error);

                if (engineStrippingSupported)
                {
                    var icallSummaryPath = Path.Combine(managedAssemblyFolderPath, "ICallSummary.txt");
                    GenerateInternalCallSummaryFile(icallSummaryPath, managedAssemblyFolderPath, tempStripPath);

                    if (performEngineStripping && !UseUnityLinkerEngineModuleStripping)
                    {
                        // Find which modules we must include in the build based on Assemblies
                        HashSet<UnityType> nativeClasses;
                        HashSet<string> nativeModules;
                        CodeStrippingUtils.GenerateDependencies(tempStripPath, icallSummaryPath, rcr, performEngineStripping, out nativeClasses, out nativeModules, platformProvider);
                        // Add module-specific blacklists.
                        addedMoreBlacklists = AddWhiteListsForModules(nativeModules, ref blacklists, platformProvider.moduleStrippingInformationFolder);
                    }
                }

                if (performEngineStripping && UseUnityLinkerEngineModuleStripping)
                    UpdateBuildReport(ReadLinkerToEditorData(tempStripPath), platformProvider);

                // If we had to add more whitelists, we need to run AssemblyStripper again with the added whitelists.
            }
            while (addedMoreBlacklists && !UseUnityLinkerEngineModuleStripping);

            // keep unstripped files for debugging purposes
            var tempUnstrippedPath = Path.GetFullPath(Path.Combine(managedAssemblyFolderPath, "tempUnstripped"));
            if (debugUnstripped)
                Directory.CreateDirectory(tempUnstrippedPath);
            foreach (var file in Directory.GetFiles(managedAssemblyFolderPath))
            {
                var extension = Path.GetExtension(file);
                if (string.Equals(extension, ".dll", StringComparison.InvariantCultureIgnoreCase) ||
                    string.Equals(extension, ".winmd", StringComparison.InvariantCultureIgnoreCase) ||
                    string.Equals(extension, ".mdb", StringComparison.InvariantCultureIgnoreCase) ||
                    string.Equals(extension, ".pdb", StringComparison.InvariantCultureIgnoreCase))
                {
                    if (debugUnstripped)
                        File.Move(file, Path.Combine(tempUnstrippedPath, Path.GetFileName(file)));
                    else
                        File.Delete(file);
                }
            }

            foreach (var file in Directory.GetFiles(tempStripPath))
                File.Move(file, Path.Combine(managedAssemblyFolderPath, Path.GetFileName(file)));
            foreach (var dir in Directory.GetDirectories(tempStripPath))
                Directory.Move(dir, Path.Combine(managedAssemblyFolderPath, Path.GetFileName(dir)));
            Directory.Delete(tempStripPath);
        }

        public static bool UseUnityLinkerEngineModuleStripping
        {
            get { return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("UNITYLINKER_DISABLE_EMS")); }
        }

        private static string WriteTypesInScenesBlacklist(string managedAssemblyDirectory, RuntimeClassRegistry rcr)
        {
            var items = rcr.GetAllManagedTypesInScenes();

            var sb = new StringBuilder();
            sb.AppendLine("<linker>");
            foreach (var assemblyTypePair in items)
            {
                // Some how stuff for assemblies that will not be in the build make it into UsedTypePerUserAssembly such as
                // ex: [UnityEditor.TestRunner.dll] UnityEditor.TestTools.TestRunner.TestListCacheData
                //
                // Filter anything out where the assembly doesn't exist so that UnityLinker can be strict about preservations in link xml files
                if (!File.Exists(Path.Combine(managedAssemblyDirectory, assemblyTypePair.Key)))
                    continue;

                sb.AppendLine($"\t<assembly fullname=\"{Path.GetFileNameWithoutExtension(assemblyTypePair.Key)}\">");
                foreach (var type in assemblyTypePair.Value)
                {
                    sb.AppendLine($"\t\t<type fullname=\"{type}\" preserve=\"nothing\"/>");
                }
                sb.AppendLine("\t</assembly>");
            }
            sb.AppendLine("</linker>");

            var path = Path.Combine(managedAssemblyDirectory, "TypesInScenes.xml");
            File.WriteAllText(path, sb.ToString());
            return path;
        }

        private static void UpdateBuildReport(LinkerToEditorData dataFromLinker, IIl2CppPlatformProvider platformProvider)
        {
            var strippingInfo = platformProvider == null ? null : StrippingInfo.GetBuildReportData(platformProvider.buildReport);

            if (strippingInfo == null)
                return;

            foreach (var moduleInfo in dataFromLinker.report.modules)
            {
                strippingInfo.AddModule(moduleInfo.name);
                foreach (var moduleDependency in moduleInfo.dependencies)
                {
                    strippingInfo.RegisterDependency(StrippingInfo.ModuleName(moduleInfo.name), moduleDependency.name);

                    if (!string.IsNullOrEmpty(moduleDependency.icon))
                        strippingInfo.SetIcon(moduleDependency.name, moduleDependency.icon);

                    // Hacky way to match the existing behavior
                    if (moduleDependency.name == "UnityConnectSettings")
                        strippingInfo.RegisterDependency(moduleDependency.name, "Required by UnityAnalytics");

                    foreach (var scene in moduleDependency.scenes)
                    {
                        strippingInfo.RegisterDependency(moduleDependency.name, scene);

                        var klass = UnityType.FindTypeByName(moduleDependency.name);
                        if (klass != null && !klass.IsDerivedFrom(CodeStrippingUtils.GameManagerTypeInfo))
                        {
                            if (scene.EndsWith(".unity"))
                                strippingInfo.SetIcon(scene, "class/SceneAsset");
                            else
                                strippingInfo.SetIcon(scene, "class/AssetBundle");
                        }
                    }
                }
            }
        }

        internal static LinkerToEditorData ReadLinkerToEditorData(string outputDirectory)
        {
            var dataPath = Path.Combine(outputDirectory, "UnityLinkerToEditorData.json");
            var contents = File.ReadAllText(dataPath);
            var data = JsonUtility.FromJson<LinkerToEditorData>(contents);
            return data;
        }

        private static string WriteEditorData(string managedAssemblyDirectory, RuntimeClassRegistry rcr)
        {
            var items = GetTypesInScenesInformation(managedAssemblyDirectory, rcr);

            List<string> forceIncludeModules;
            List<string> forceExcludeModules;
            CollectIncludedAndExcludedModules(out forceIncludeModules, out forceExcludeModules);

            var editorToLinkerData = new EditorToLinkerData
            {
                typesInScenes = items.ToArray(),
                allNativeTypes = CollectNativeTypeData().ToArray(),
                forceIncludeModules = forceIncludeModules.ToArray(),
                forceExcludeModules = forceExcludeModules.ToArray()
            };

            var dataPath = Path.Combine(managedAssemblyDirectory, "EditorToUnityLinkerData.json");
            File.WriteAllText(dataPath, JsonUtility.ToJson(editorToLinkerData, true));
            return dataPath;
        }

        static List<EditorToLinkerData.TypeInSceneData> GetTypesInScenesInformation(string managedAssemblyDirectory, RuntimeClassRegistry rcr)
        {
            var items = new List<EditorToLinkerData.TypeInSceneData>();
            foreach (var nativeClass in rcr.GetAllNativeClassesIncludingManagersAsString())
            {
                var unityType = UnityType.FindTypeByName(nativeClass);

                var managedName = RuntimeClassMetadataUtils.ScriptingWrapperTypeNameForNativeID(unityType.persistentTypeID);
                var usedInScenes = rcr.GetScenesForClass(unityType.persistentTypeID);

                bool noManagedType = unityType.persistentTypeID != 0 && managedName == "UnityEngine.Object";
                var information = new EditorToLinkerData.TypeInSceneData(
                    noManagedType ? null : "UnityEngine.dll",
                    noManagedType ? null : managedName,
                    nativeClass,
                    unityType.module,
                    usedInScenes != null ? usedInScenes.ToArray() : null);

                items.Add(information);
            }

            foreach (var userAssembly in rcr.UsedTypePerUserAssembly)
            {
                // Some how stuff for assemblies that will not be in the build make it into UsedTypePerUserAssembly such as
                // ex: [UnityEditor.TestRunner.dll] UnityEditor.TestTools.TestRunner.TestListCacheData
                //
                // Filter anything out where the assembly doesn't exist so that UnityLinker can be strict about being able to find
                // all of the types that are reported as being in the scene.
                if (!File.Exists(Path.Combine(managedAssemblyDirectory, userAssembly.Key)))
                    continue;

                foreach (var type in userAssembly.Value)
                    items.Add(new EditorToLinkerData.TypeInSceneData(userAssembly.Key, type, null, null, null));
            }

            return items;
        }

        static List<EditorToLinkerData.NativeTypeData> CollectNativeTypeData()
        {
            var items = new List<EditorToLinkerData.NativeTypeData>();
            foreach (var unityType in UnityType.GetTypes())
            {
                items.Add(new EditorToLinkerData.NativeTypeData
                {
                    name = unityType.name,
                    module = unityType.module
                });
            }

            return items;
        }

        static void CollectIncludedAndExcludedModules(out List<string> forceInclude, out List<string> forceExclude)
        {
            forceInclude = new List<string>();
            forceExclude = new List<string>();
            // Apply manual stripping overrides
            foreach (var module in ModuleMetadata.GetModuleNames())
            {
                var includeSetting = ModuleMetadata.GetModuleIncludeSettingForModule(module);
                if (includeSetting == ModuleIncludeSetting.ForceInclude)
                    forceInclude.Add(module);
                else if (includeSetting == ModuleIncludeSetting.ForceExclude)
                    forceExclude.Add(module);
            }
        }

        private static string WriteMethodsToPreserveBlackList(RuntimeClassRegistry rcr, BuildTarget target)
        {
            var contents = GetMethodPreserveBlacklistContents(rcr, target);
            if (contents == null)
                return null;
            var methodPerserveBlackList = Path.GetTempFileName();
            File.WriteAllText(methodPerserveBlackList, contents);
            return methodPerserveBlackList;
        }

        private static string GetMethodPreserveBlacklistContents(RuntimeClassRegistry rcr, BuildTarget target)
        {
            if (rcr.GetMethodsToPreserve().Count == 0)
                return null;

            var sb = new StringBuilder();
            sb.AppendLine("<linker>");

            var groupedByAssembly = rcr.GetMethodsToPreserve().GroupBy(m => m.assembly);
            foreach (var assembly in groupedByAssembly)
            {
                var assemblyName = assembly.Key;
                sb.AppendLine(string.Format("\t<assembly fullname=\"{0}\" ignoreIfMissing=\"1\">", assemblyName));
                var groupedByType = assembly.GroupBy(m => m.fullTypeName);
                foreach (var type in groupedByType)
                {
                    sb.AppendLine(string.Format("\t\t<type fullname=\"{0}\">", type.Key));
                    foreach (var method in type)
                        sb.AppendLine(string.Format("\t\t\t<method name=\"{0}\"/>", method.methodName));
                    sb.AppendLine("\t\t</type>");
                }
                sb.AppendLine("\t</assembly>");
            }

            sb.AppendLine("</linker>");
            return sb.ToString();
        }

        static public void StripForMonoBackend(BuildTarget buildTarget, RuntimeClassRegistry usedClasses, ManagedStrippingLevel managedStrippingLevel, BuildReport report)
        {
            if (managedStrippingLevel == ManagedStrippingLevel.Disabled)
                return;

            var stagingAreaData = Paths.Combine("Temp", "StagingArea", "Data");

            var platformProvider = new BaseIl2CppPlatformProvider(buildTarget, Path.Combine(stagingAreaData, "Libraries"), report);

            var managedAssemblyFolderPath = Path.GetFullPath(Path.Combine(stagingAreaData, "Managed"));
            AssemblyStripper.StripAssemblies(managedAssemblyFolderPath, platformProvider, usedClasses, managedStrippingLevel);
        }
    }
}
