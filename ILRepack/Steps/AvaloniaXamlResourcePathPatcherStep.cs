//
// Copyright (c) 2015 Timotei Dolean
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ILRepacking.Steps
{
    //TODO: Maybe we should fix this in *all* (xaml) files?
    internal class AvaloniaXamlResourcePathPatcherStep : IRepackStep
    {
        private readonly ILogger _logger;
        private readonly IRepackContext _repackContext;
        private static readonly Regex AssemblyRegex = new Regex("avares://([^/]+?)/", RegexOptions.IgnoreCase);

        public AvaloniaXamlResourcePathPatcherStep(ILogger logger, IRepackContext repackContext)
        {
            _logger = logger;
            _repackContext = repackContext;
        }

        public void Perform()
        {
            var relevantTypes = GetTypesWhichMayContainPackUris();

            _logger.Verbose("Processing Avalonia XAML resource paths ...");
            foreach (var type in relevantTypes)
            {
                PatchAvaresInClrStrings(type);
            }
        }

        private IEnumerable<TypeDefinition> GetTypesWhichMayContainPackUris()
        {
            var types = _repackContext.TargetAssemblyDefinition.Modules.SelectMany(m => m.Types);

            var isModuleReferencingWpfMap = new Dictionary<ModuleDefinition, bool>();

            foreach (var type in types)
            {
                var originalModule = _repackContext.MappingHandler.GetOriginalModule(type);
                if (!isModuleReferencingWpfMap.TryGetValue(originalModule, out var isReferencingWpf))
                {
                    isModuleReferencingWpfMap[originalModule] = isReferencingWpf = IsModuleDefinitionReferencingWpf(originalModule);
                }

                if (!isReferencingWpf)
                {
                    continue;
                }

                yield return type;
            }
        }

        private bool IsModuleDefinitionReferencingWpf(ModuleDefinition module)
        {
            // checking for PresentationFramework instead of PresentationCore, as for example
            // AnotherClassLibrary only references PresenationFramework but not PresentationCore
            return module.AssemblyReferences.Any(y => y.Name == "Avalonia.Base");
        }

        private void PatchAvaresInClrStrings(TypeDefinition type)
        {
            foreach (var method in type.Methods.Where(x => x.HasBody))
            {
                PatchMethod(method);
            }
        }

        private void PatchMethod(MethodDefinition method)
        {
            foreach (var stringInstruction in method.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldstr))
            {
                string path = stringInstruction.Operand as string;
                if (string.IsNullOrEmpty(path))
                    continue;

                var type = method.DeclaringType;
                var originalScope = _repackContext.MappingHandler.GetOrigTypeScope<ModuleDefinition>(type);

                stringInstruction.Operand = PatchPath(
                    path,
                    _repackContext.PrimaryAssemblyDefinition,
                    originalScope.Assembly,
                    _repackContext.OtherAssemblies);
            }
        }

        internal static string PatchPath(
            string path,
            AssemblyDefinition primaryAssembly,
            AssemblyDefinition sourceAssembly,
            IList<AssemblyDefinition> otherAssemblies)
        {
            if (string.IsNullOrEmpty(path) || !(path.StartsWith("/") || path.StartsWith("avares://")))
                return path;

            string patchedPath = path;
            if (primaryAssembly == sourceAssembly)
            {
                if (otherAssemblies.Any(assembly => TryPatchPath(path, primaryAssembly, assembly, otherAssemblies, true, out patchedPath)))
                    return patchedPath;

                return path;
            }

            if (TryPatchPath(path, primaryAssembly, sourceAssembly, otherAssemblies, false, out patchedPath))
                return patchedPath;

            if (!path.EndsWith(".xaml") && !path.EndsWith(".axaml"))
                return path;

            // we've got no non-primary assembly knowledge so far,
            // that means it's a relative path in the source assembly -> just add the assembly's name as subdirectory
            // /themes/file.xaml -> /library/themes/file.xaml
            return "/" + sourceAssembly.Name.Name + path;
        }

        private static bool TryPatchPath(
            string path,
            AssemblyDefinition primaryAssembly,
            AssemblyDefinition referenceAssembly,
            IList<AssemblyDefinition> otherAssemblies,
            bool isPrimarySameAsSource,
            out string patchedPath)
        {

            // /library/file.xaml -> /primary/library/file.xaml
            if (isPrimarySameAsSource)
            {
                string referenceAssemblyPath = "avares://" + GetAssemblyPath(referenceAssembly) + "/";
                string newPath = "avares://" + GetAssemblyPath(primaryAssembly) + "/" + referenceAssembly.Name.Name + "/";

                patchedPath = path.Replace(referenceAssemblyPath, newPath);
            }
            else
            {
                patchedPath = AssemblyRegex.Replace(path, m =>
                {
                    if (m.Groups.Count == 2)
                    {
                        if (otherAssemblies.Any(a => a.Name.Name == m.Groups[1].Value))
                        {
                            return "avares://" + GetAssemblyPath(primaryAssembly) + "/" + m.Groups[1].Value + "/";
                        }
                        else
                        {
                            return m.Value;
                        }
                    }
                    else
                    {
                        return m.Value;
                    }
                });
            }

            // if they're modified, we're good!
            var isNotSame = !ReferenceEquals(patchedPath, path);
            if (isNotSame)
            {
            }
            return isNotSame;
        }

        private static string GetAssemblyPath(AssemblyDefinition sourceAssembly)
        {
            return string.Format("{0}", sourceAssembly.Name.Name);
        }
    }
}
