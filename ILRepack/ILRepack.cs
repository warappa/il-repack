﻿//
// Copyright (c) 2011 Francois Valdy
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
//
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using ILRepacking.Mixins;
using ILRepacking.Steps;
using ILRepacking.Steps.SourceServerData;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.PE;

namespace ILRepacking
{
    public class ILRepack : IRepackContext
    {
        internal RepackOptions Options;
        internal ILogger Logger;

        internal IList<string> MergedAssemblyFiles { get; set; }
        internal string PrimaryAssemblyFile { get; set; }

        // contains all 'other' assemblies, but not the primary assembly
        internal IList<AssemblyDefinition> OtherAssemblies { get; private set; }
        // contains all assemblies, primary (first one) and 'other'
        internal IList<AssemblyDefinition> MergedAssemblies { get; private set; }

        internal AssemblyDefinition TargetAssemblyDefinition { get; private set; }
        internal AssemblyDefinition PrimaryAssemblyDefinition { get; private set; }
        internal RepackAssemblyResolver GlobalAssemblyResolver { get; } = new RepackAssemblyResolver();

        internal ModuleDefinition TargetAssemblyMainModule => TargetAssemblyDefinition.MainModule;
        internal ModuleDefinition PrimaryAssemblyMainModule => PrimaryAssemblyDefinition.MainModule;

        // We need to avoid exposing Cecil types in our public API, so that all of Cecil can be internalized.
        // See https://github.com/gluck/il-repack/issues/358.
        RepackAssemblyResolver IRepackContext.GlobalAssemblyResolver => GlobalAssemblyResolver;
        ModuleDefinition IRepackContext.TargetAssemblyMainModule => TargetAssemblyMainModule;
        AssemblyDefinition IRepackContext.TargetAssemblyDefinition => TargetAssemblyDefinition;
        AssemblyDefinition IRepackContext.PrimaryAssemblyDefinition => PrimaryAssemblyDefinition;
        ModuleDefinition IRepackContext.PrimaryAssemblyMainModule => PrimaryAssemblyMainModule;
        IList<AssemblyDefinition> IRepackContext.MergedAssemblies => MergedAssemblies;
        IList<AssemblyDefinition> IRepackContext.OtherAssemblies => OtherAssemblies;

        private IKVMLineIndexer _lineIndexer;
        private ReflectionHelper _reflectionHelper;
        private PlatformFixer _platformFixer;
        private MappingHandler _mappingHandler;

        private static readonly Regex TypeRegex = new Regex("^(.*?), ([^>,]+), .*$");

        IKVMLineIndexer IRepackContext.LineIndexer => _lineIndexer;
        ReflectionHelper IRepackContext.ReflectionHelper => _reflectionHelper;
        PlatformFixer IRepackContext.PlatformFixer => _platformFixer;
        MappingHandler IRepackContext.MappingHandler => _mappingHandler;
        private readonly Dictionary<AssemblyDefinition, int> _aspOffsets = new Dictionary<AssemblyDefinition, int>();

        private readonly RepackImporter _repackImporter;

        public ILRepack(RepackOptions options)
            : this(options, new RepackLogger())
        {
        }

        public ILRepack(RepackOptions options, ILogger logger)
        {
            Options = options;
            Logger = logger;

            if (logger is RepackLogger repackLogger && repackLogger.Open(options.LogFile))
            {
                options.Log = true;
            }

            logger.ShouldLogVerbose = options.LogVerbose;

            if (logger.ShouldLogVerbose)
            {
                GlobalAssemblyResolver.AssemblyResolved += (assemblyName, filePath) =>
                {
                    logger.Verbose($"Resolved '{assemblyName}' from '{filePath}'");
                };
            }

            _repackImporter = new RepackImporter(Logger, Options, this, _aspOffsets);
        }

        private void ReadInputAssemblies()
        {
            OtherAssemblies = new List<AssemblyDefinition>();
            // TODO: this could be parallelized to gain speed
            var primary = MergedAssemblyFiles.FirstOrDefault();
            var debugSymbolsRead = false;
            foreach (string assembly in MergedAssemblyFiles)
            {
                var result = ReadInputAssembly(assembly, primary == assembly);
                if (result.IsPrimary)
                {
                    PrimaryAssemblyDefinition = result.Definition;
                    PrimaryAssemblyFile = result.Assembly;
                }
                else
                    OtherAssemblies.Add(result.Definition);

                debugSymbolsRead |= result.SymbolsRead;
            }
            // prevent writing PDB if we haven't read any
            Options.DebugInfo = debugSymbolsRead;

            MergedAssemblies = new List<AssemblyDefinition>(OtherAssemblies);
            MergedAssemblies.Insert(0, PrimaryAssemblyDefinition);
        }

        private AssemblyDefinitionContainer ReadInputAssembly(string assembly, bool isPrimary)
        {
            Logger.Verbose("Adding assembly for merge: " + assembly);
            try
            {
                ReaderParameters rp = new ReaderParameters(ReadingMode.Immediate) { AssemblyResolver = GlobalAssemblyResolver };
                // read PDB/MDB?
                if (Options.DebugInfo)
                {
                    rp.ReadSymbols = true;
                    rp.SymbolReaderProvider = new DefaultSymbolReaderProvider(throwIfNoSymbol: false);
                }
                AssemblyDefinition mergeAsm;
                try
                {
                    mergeAsm = AssemblyDefinition.ReadAssembly(assembly, rp);
                }
                catch (BadImageFormatException e) when (!rp.ReadSymbols)
                {
                    throw new InvalidOperationException(
                        "ILRepack does not support merging non-.NET libraries (e.g.: native libraries)", e);
                }
                // cope with invalid symbol file
                catch (Exception ex) when (rp.ReadSymbols)
                {
                    rp.ReadSymbols = false;
                    rp.SymbolReaderProvider = null;
                    try
                    {
                        mergeAsm = AssemblyDefinition.ReadAssembly(assembly, rp);
                    }
                    catch (BadImageFormatException e)
                    {
                        throw new InvalidOperationException(
                            "ILRepack does not support merging non-.NET libraries (e.g.: native libraries)", e);
                    }

                    Logger.Warn($"Failed to load debug information for {assembly}:{Environment.NewLine}{ex}");
                }

                if (!Options.AllowZeroPeKind && (mergeAsm.MainModule.Attributes & ModuleAttributes.ILOnly) == 0)
                    throw new ArgumentException("Failed to load assembly with Zero PeKind: " + assembly);
                GlobalAssemblyResolver.RegisterAssembly(mergeAsm);

                return new AssemblyDefinitionContainer
                {
                    Assembly = assembly,
                    Definition = mergeAsm,
                    IsPrimary = isPrimary,
                    SymbolsRead = rp.ReadSymbols
                };
            }
            catch (Exception ex)
            {
                Logger.Error($"Loading {assembly} failed: {ex.Message}");
                throw;
            }
        }

        IMetadataScope IRepackContext.MergeScope(IMetadataScope scope)
        {
            if (scope is AssemblyNameReference)
                return TargetAssemblyMainModule.AssemblyReferences.AddUniquely((Mono.Cecil.AssemblyNameReference)scope);
            Logger.Warn("Merging a module scope, probably not supported");
            return scope;
        }

        internal class AssemblyDefinitionContainer
        {
            public bool SymbolsRead { get; set; }
            public AssemblyDefinition Definition { get; set; }
            public string Assembly { get; set; }
            public bool IsPrimary { get; set; }
        }

        public enum Kind
        {
            Dll,
            Exe,
            WinExe,
            SameAsPrimaryAssembly
        }


        private TargetRuntime ParseTargetPlatform()
        {
            TargetRuntime runtime = PrimaryAssemblyMainModule.Runtime;
            if (Options.TargetPlatformVersion != null)
            {
                switch (Options.TargetPlatformVersion)
                {
                    case "v2": runtime = TargetRuntime.Net_2_0; break;
                    case "v4": runtime = TargetRuntime.Net_4_0; break;
                    default: throw new ArgumentException($"Invalid TargetPlatformVersion: '{Options.TargetPlatformVersion}'");
                }
                _platformFixer.ParseTargetPlatformDirectory(runtime, Options.TargetPlatformDirectory);
            }
            return runtime;
        }

        private string ResolveTargetPlatformDirectory(string version)
        {
            if (version == null)
                return null;
            var platformBasePath = Path.GetDirectoryName(Path.GetDirectoryName(typeof(string).Assembly.Location));
            List<string> platformDirectories = new List<string>(Directory.GetDirectories(platformBasePath));
            var platformDir = version.Substring(1);
            if (platformDir.Length == 1) platformDir = platformDir + ".0";
            // mono platform dir is '2.0' while windows is 'v2.0.50727'
            var targetPlatformDirectory = platformDirectories
                .FirstOrDefault(x => Path.GetFileName(x).StartsWith(platformDir) || Path.GetFileName(x).StartsWith($"v{platformDir}"));
            if (targetPlatformDirectory == null)
                throw new ArgumentException($"Failed to find target platform '{Options.TargetPlatformVersion}' in '{platformBasePath}'");
            Logger.Verbose($"Target platform directory resolved to {targetPlatformDirectory}");
            return targetPlatformDirectory;
        }

        public static IEnumerable<AssemblyName> GetRepackAssemblyNames(Type typeInRepackedAssembly)
        {
            try
            {
                using (Stream stream = typeInRepackedAssembly.Assembly.GetManifestResourceStream(ResourcesRepackStep.ILRepackListResourceName))
                    if (stream != null)
                    {
                        string[] list = ResourcesRepackStep.GetRepackListFromStream(stream);
                        return list.Select(x => new AssemblyName(x));
                    }
            }
            catch (Exception)
            {
            }
            return Enumerable.Empty<AssemblyName>();
        }

        public static AssemblyName GetRepackAssemblyName(IEnumerable<AssemblyName> repackAssemblyNames, string repackedAssemblyName, Type fallbackType)
        {
            return repackAssemblyNames?.FirstOrDefault(name => name.Name == repackedAssemblyName) ?? fallbackType.Assembly.GetName();
        }

        void PrintRepackHeader()
        {
            var assemblies = GetRepackAssemblyNames(typeof(ILRepack));
            var ilRepack = GetRepackAssemblyName(assemblies, "ILRepack", typeof(ILRepack));
            Logger.Verbose($"IL Repack - Version {ilRepack.Version.ToString(3)}");
            Logger.Verbose($"Runtime: {typeof(ILRepack).Assembly.FullName}");
            Logger.Info(Options.ToCommandLine());
        }

        /// <summary>
        /// The actual repacking process, called by main after parsing arguments.
        /// When referencing this assembly, call this after setting the merge properties.
        /// </summary>
        public void Repack()
        {
            Options.Validate();

            string outputFilePath = Options.OutputFile;
            var outputDir = Path.GetDirectoryName(outputFilePath);
            var tempOutputDirectory = Path.Combine(outputDir, $"ILRepack-{Process.GetCurrentProcess().Id}-{DateTime.UtcNow.Ticks.ToString().Substring(12)}");
            EnsureDirectoryExists(tempOutputDirectory);

            try
            {
                RepackCore(tempOutputDirectory);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempOutputDirectory))
                    {
                        Directory.Delete(tempOutputDirectory, recursive: true);
                    }
                }
                catch
                {
                }
            }
        }

        private void RepackCore(string tempOutputDirectory)
        {
            var timer = new Stopwatch();
            timer.Start();
            MergedAssemblyFiles = Options.ResolveFiles();
            foreach (var inputFile in MergedAssemblyFiles)
            {
                if (!File.Exists(inputFile))
                {
                    throw new FileNotFoundException($"Input file not found: {inputFile}");
                }
            }

            PrintRepackHeader();

            string outputFilePath = Options.OutputFile;
            var outputDir = Path.GetDirectoryName(outputFilePath);
            string tempOutputFilePath = Path.Combine(tempOutputDirectory, Path.GetFileName(outputFilePath));
            Options.OutputFile = tempOutputFilePath;

            _reflectionHelper = new ReflectionHelper(this);
            ResolveSearchDirectories();

            // Read input assemblies only after all properties are set.
            ReadInputAssemblies();

            if (!Options.KeepOtherVersionReferences)
            {
                _platformFixer = new PlatformAndDuplicateFixer(this, PrimaryAssemblyMainModule.Runtime);
            }
            else
            {
                _platformFixer = new PlatformFixer(this, PrimaryAssemblyMainModule.Runtime);
            }

            _mappingHandler = new MappingHandler();
            bool hadStrongName = PrimaryAssemblyDefinition.Name.HasPublicKey;

            ModuleKind kind = PrimaryAssemblyMainModule.Kind;
            if (Options.TargetKind.HasValue)
            {
                switch (Options.TargetKind.Value)
                {
                    case Kind.Dll: kind = ModuleKind.Dll; break;
                    case Kind.Exe: kind = ModuleKind.Console; break;
                    case Kind.WinExe: kind = ModuleKind.Windows; break;
                }
            }
            TargetRuntime runtime = ParseTargetPlatform();

            // change assembly's name to correspond to the file we create
            string mainModuleName = Path.GetFileNameWithoutExtension(Options.OutputFile);

            if (TargetAssemblyDefinition == null)
            {
                AssemblyNameDefinition asmName = Clone(PrimaryAssemblyDefinition.Name);
                asmName.Name = mainModuleName;
                TargetAssemblyDefinition = AssemblyDefinition.CreateAssembly(asmName, mainModuleName,
                    new ModuleParameters()
                    {
                        Kind = kind,
                        Architecture = PrimaryAssemblyMainModule.Architecture,
                        AssemblyResolver = GlobalAssemblyResolver,
                        Runtime = runtime
                    });
            }
            else
            {
                // TODO: does this work or is there more to do?
                TargetAssemblyMainModule.Kind = kind;
                TargetAssemblyMainModule.Runtime = runtime;

                TargetAssemblyDefinition.Name.Name = mainModuleName;
                TargetAssemblyMainModule.Name = mainModuleName;
            }
            // set the main module attributes
            TargetAssemblyMainModule.Attributes = PrimaryAssemblyMainModule.Attributes;
            TargetAssemblyMainModule.Win32ResourceDirectory = MergeWin32Resources();

            if (Options.Version != null)
                TargetAssemblyDefinition.Name.Version = Options.Version;

            _lineIndexer = new IKVMLineIndexer(this, Options.LineIndexation);
            var signingStep = new SigningStep(this, Options);

            bool isUnixEnvironment = Environment.OSVersion.Platform == PlatformID.MacOSX || Environment.OSVersion.Platform == PlatformID.Unix;
            bool needToCopyPermissions = isUnixEnvironment && (kind == ModuleKind.Console || kind == ModuleKind.Windows);
            string permissionsText = null;

            string primaryFilePath = Path.GetFullPath(PrimaryAssemblyFile);
            string primaryDirectory = Path.GetDirectoryName(primaryFilePath);

            if (needToCopyPermissions)
            {
                var process = ProcessRunner.Run("stat", $"-f \"%Lp\" \"{primaryFilePath}\"", primaryDirectory);
                var output = process.Output.Trim('\r', '\n', ' ');
                if (int.TryParse(output, out int permissions))
                {
                    permissionsText = output;
                }

                Logger.Verbose($"stat \"{primaryFilePath}\" returned {output} (error: {process.ErrorOutput}, exit code: {process.ExitCode})");
            }

            using (var sourceServerDataStep = GetSourceServerDataStep(isUnixEnvironment))
            {
                List<IRepackStep> repackSteps = new List<IRepackStep>
                {
                    signingStep,
                    new ReferencesRepackStep(Logger, this, Options),
                    new TypesRepackStep(Logger, this, _repackImporter, Options),
                    new ILLinkFileMergeStep(Logger, this, Options),
                    new ResourcesRepackStep(Logger, this, Options),
                    new AttributesRepackStep(Logger, this, _repackImporter, Options),
                    new ReferencesFixStep(Logger, this, _repackImporter, Options),
                    new PublicTypesFixStep(Logger, this),
                    new XamlResourcePathPatcherStep(Logger, this),
                    new AvaloniaXamlResourcePathPatcherStep(Logger, this),
                    new SourceLinkStep(Logger, this),
                    sourceServerDataStep
                };

                foreach (var step in repackSteps)
                {
                    step.Perform();
                }

                var anySymbolReader = MergedAssemblies
                    .Select(m => m.MainModule.SymbolReader)
                    .Where(r => r != null)
                    .FirstOrDefault();
                var symbolWriterProvider = anySymbolReader?.GetWriterProvider();
                var parameters = new WriterParameters
                {
                    StrongNameKeyPair = signingStep.KeyInfo?.KeyPair,
                    StrongNameKeyBlob = signingStep.KeyInfo?.KeyBlob,
                    WriteSymbols = Options.DebugInfo && symbolWriterProvider != null,
                    SymbolWriterProvider = symbolWriterProvider,
                    DeterministicMvid = true
                };

                if (!Options.PreserveTimestamp)
                {
                    parameters.Timestamp = ComputeDeterministicTimestamp();
                }

                Logger.Verbose($"Writing temporary assembly: {tempOutputFilePath}");
                TargetAssemblyDefinition.Write(tempOutputFilePath, parameters);

                sourceServerDataStep.Write();

                foreach (var assembly in MergedAssemblies)
                {
                    assembly.Dispose();
                }

                TargetAssemblyDefinition.Dispose();
                GlobalAssemblyResolver.Dispose();

                MoveTempFile(tempOutputDirectory, outputDir);

                Options.OutputFile = outputFilePath;

                // If this is an executable and we are on linux/osx we should copy file permissions from
                // the primary assembly
                if (permissionsText != null)
                {
                    var process = ProcessRunner.Run("chmod", $"{permissionsText} \"{Options.OutputFile}\"");
                    if (process.ExitCode < 0)
                    {
                        Logger.Warn($"Call to chmod {permissionsText} \"{Options.OutputFile}\" returned {process.ExitCode}");
                    }
                    else
                    {
                        Logger.Verbose($"chmod {permissionsText} \"{Options.OutputFile}\"");
                    }
                }

                if (hadStrongName && !TargetAssemblyDefinition.Name.HasPublicKey)
                    Options.StrongNameLost = true;

                // nice to have, merge .config (assembly configuration file) & .xml (assembly documentation)
                if (Options.SkipConfigMerge == false)
                {
                    ConfigMerger.Process(this);
                }
                if (Options.XmlDocumentation)
                    DocumentationMerger.Process(this);
            }

            if (File.Exists(Options.OutputFile))
            {
                Logger.Info($"Wrote {Options.OutputFile}");
            }
            else
            {
                Logger.Info($"Failed to write {Options.OutputFile}");
            }

            Logger.Verbose($"Finished in {timer.Elapsed}");
            if (Logger is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        private uint ComputeDeterministicTimestamp()
        {
            var sb = new StringBuilder();

            foreach (var assembly in this.MergedAssemblies)
            {
                var mvid = assembly.MainModule.Mvid;
                sb.Append(mvid.ToString());
            }

            var text = sb.ToString();

            return ComputeHash(text);
        }

        static uint ComputeHash(string text)
        {
            const uint Offset = 2166136261;
            const uint Prime = 16777619;
        
            uint hash = Offset;

            unchecked
            {
                for (int i = 0; i < text.Length; i++)
                {
                    char ch = text[i];
                    hash = (hash ^ ch) * Prime;
                }
            }

            return hash;
        }

        private void MoveTempFile(string tempDirectory, string finalDirectory)
        {
            foreach (var sourceFilePath in Directory.GetFiles(tempDirectory))
            {
                var fileName = Path.GetFileName(sourceFilePath);
                var targetFilePath = Path.Combine(finalDirectory, fileName);

                // delete the destination first if it's a hardlink, otherwise 
                // we'll accidentally overwrite the original of the hardlink
                if (File.Exists(targetFilePath))
                {
                    File.Delete(targetFilePath);
                }

                File.Move(sourceFilePath, targetFilePath);
            }

            Directory.Delete(tempDirectory, recursive: true);
        }

        private void EnsureDirectoryExists(string directory)
        {
            Directory.CreateDirectory(directory);
        }

        private ISourceServerDataRepackStep GetSourceServerDataStep(bool isUnixEnvironment)
        {
            if (isUnixEnvironment)
            {
                return new NullSourceServerStep(Logger);
            }
            else
            {
                return new SourceServerDataRepackStep(Options.OutputFile, MergedAssemblyFiles);
            }
        }

        private void ResolveSearchDirectories()
        {
            var directories = new List<string>();

            foreach (var searchDirectory in Options.SearchDirectories)
            {
                directories.Add(searchDirectory);
            }

            if (directories.Count == 0)
            {
                foreach (var input in MergedAssemblyFiles)
                {
                    if (!Path.IsPathRooted(input))
                    {
                        continue;
                    }

                    var directory = Path.GetDirectoryName(input);
                    directory = Path.GetFullPath(directory);
                    directories.Add(directory);
                }
            }

            var targetPlatformDirectory = Options.TargetPlatformDirectory ?? ResolveTargetPlatformDirectory(Options.TargetPlatformVersion);
            if (targetPlatformDirectory != null)
            {
                directories.Add(targetPlatformDirectory);
                var facadesDirectory = Path.Combine(targetPlatformDirectory, "Facades");
                if (Directory.Exists(facadesDirectory))
                {
                    directories.Add(facadesDirectory);
                }
            }

            directories = directories.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var dir in directories)
            {
                GlobalAssemblyResolver.AddSearchDirectory(dir);
            }
        }

        private ResourceDirectory MergeWin32Resources()
        {
            var primary = PrimaryAssemblyMainModule.ReadWin32ResourceDirectory() ?? new ResourceDirectory();

            foreach (var ass in OtherAssemblies)
            {
                var directory = ass.MainModule.ReadWin32ResourceDirectory();
                if (directory != null)
                {
                    MergeDirectory(new List<ResourceEntry>(), primary, ass, directory);
                }
            }
            return primary;
        }

        private void MergeDirectory(List<ResourceEntry> parents, ResourceDirectory ret, AssemblyDefinition ass, ResourceDirectory directory)
        {
            foreach (var entry in directory.Entries)
            {
                var exist = ret.Entries.FirstOrDefault(x => entry.Name == null ? entry.Id == x.Id : entry.Name == x.Name);
                if (exist == null)
                    ret.Entries.Add(entry);
                else
                    MergeEntry(parents, exist, ass, entry);
            }
        }

        private void MergeEntry(List<ResourceEntry> parents, ResourceEntry exist, AssemblyDefinition ass, ResourceEntry entry)
        {
            if (exist.Data != null && entry.Data != null)
            {
                if (IsAspResourceEntry(parents, exist))
                {
                    _aspOffsets[ass] = exist.Data.Length;
                    byte[] newData = new byte[exist.Data.Length + entry.Data.Length];
                    Array.Copy(exist.Data, 0, newData, 0, exist.Data.Length);
                    Array.Copy(entry.Data, 0, newData, exist.Data.Length, entry.Data.Length);
                    exist.Data = newData;
                }
                else if (!IsVersionInfoResource(parents, exist))
                {
                    Logger.Warn(string.Format("Duplicate Win32 resource with id={0}, parents=[{1}], name={2} in assembly {3}, ignoring", entry.Id, string.Join(",", parents.Select(p => p.Name ?? p.Id.ToString()).ToArray()), entry.Name, ass.Name));
                }
                return;
            }
            if (exist.Data != null || entry.Data != null)
            {
                Logger.Warn("Inconsistent Win32 resources, ignoring");
                return;
            }
            parents.Add(exist);
            MergeDirectory(parents, exist.Directory, ass, entry.Directory);
            parents.RemoveAt(parents.Count - 1);
        }

        private static bool IsAspResourceEntry(List<ResourceEntry> parents, ResourceEntry exist)
        {
            return exist.Id == 101 && parents.Count == 1 && parents[0].Id == 3771;
        }

        private static bool IsVersionInfoResource(List<ResourceEntry> parents, ResourceEntry exist)
        {
            return exist.Id == 0 && parents.Count == 2 && parents[0].Id == 16 && parents[1].Id == 1;
        }

        string IRepackContext.FixStr(string content)
        {
            return FixStr(content, false);
        }

        string IRepackContext.FixReferenceInIkvmAttribute(string content)
        {
            return FixStr(content, true);
        }

        private string FixStr(string content, bool javaAttribute)
        {
            if (String.IsNullOrEmpty(content) || content.Length > 512 || content.IndexOf(", ") == -1 || content.StartsWith("System."))
                return content;
            // TODO fix "TYPE, ASSEMBLYNAME, CULTURE" pattern
            // TODO fix "TYPE, ASSEMBLYNAME, VERSION, CULTURE, TOKEN" pattern
            var match = TypeRegex.Match(content);
            if (match.Success)
            {
                string type = match.Groups[1].Value;
                string targetAssemblyName = TargetAssemblyDefinition.FullName;
                if (javaAttribute)
                    targetAssemblyName = targetAssemblyName.Replace('.', '/') + ";";

                if (MergedAssemblies.Any(x => x.Name.Name == match.Groups[2].Value))
                {
                    return type + ", " + targetAssemblyName;
                }
            }
            return content;
        }

        string IRepackContext.FixTypeName(string assemblyName, string typeName)
        {
            // TODO handle renames
            return typeName;
        }

        string IRepackContext.FixAssemblyName(string assemblyName)
        {
            if (MergedAssemblies.Any(x => x.FullName == assemblyName))
            {
                // TODO no public key token !
                return TargetAssemblyDefinition.FullName;
            }
            return assemblyName;
        }

        private AssemblyNameDefinition Clone(AssemblyNameDefinition assemblyName)
        {
            AssemblyNameDefinition asmName = new AssemblyNameDefinition(assemblyName.Name, assemblyName.Version);
            asmName.Attributes = assemblyName.Attributes;
            asmName.Culture = assemblyName.Culture;
            asmName.Hash = assemblyName.Hash;
            asmName.HashAlgorithm = assemblyName.HashAlgorithm;
            asmName.PublicKey = assemblyName.PublicKey;
            asmName.PublicKeyToken = assemblyName.PublicKeyToken;
            return asmName;
        }

        TypeDefinition IRepackContext.GetMergedTypeFromTypeRef(TypeReference reference)
        {
            return _mappingHandler.GetRemappedType(reference);
        }

        TypeReference IRepackContext.GetExportedTypeFromTypeRef(TypeReference type)
        {
            return _mappingHandler.GetExportedRemappedType(type) ?? type;
        }
    }
}
