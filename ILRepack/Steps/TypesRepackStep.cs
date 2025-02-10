//
// Copyright (c) 2011 Francois Valdy
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

using System;
using System.Collections.Generic;
using Mono.Cecil;
using System.Linq;
using System.Text.RegularExpressions;
using Mono.Cecil.Cil;

namespace ILRepacking.Steps
{
    internal class TypesRepackStep : IRepackStep
    {
        private readonly ILogger _logger;
        private readonly IRepackContext _repackContext;
        private readonly IRepackImporter _repackImporter;
        private readonly RepackOptions _repackOptions;
        private List<TypeDefinition> _allTypes;

        public TypesRepackStep(
            ILogger logger,
            IRepackContext repackContext,
            IRepackImporter repackImporter,
            RepackOptions repackOptions)
        {
            _logger = logger;
            _repackContext = repackContext;
            _repackImporter = repackImporter;
            _repackOptions = repackOptions;

            _allTypes =
                _repackContext.OtherAssemblies.Concat(new[] { _repackContext.PrimaryAssemblyDefinition })
                    .SelectMany(x => x.Modules)
                    .SelectMany(m => m.Types)
                    .ToList();
        }


        /// <summary>
        /// Sucht in allen übergebenen Assemblys nach einer statischen Methode namens "<Module>" in der Klasse "<Module>"
        /// und fügt in der Haupt-Assembly einen neuen Module-Initializer ein, der alle gefundenen Methoden aufruft.
        /// </summary>
        /// <param name="mainAssembly">Die Assembly, in der der kombinierte Module-Initializer angelegt wird.</param>
        /// <param name="assemblies">Die Assemblys, aus denen die Module-Initializer eingesammelt werden.</param>
        static void MergeModuleInitializers(IEnumerable<ModuleDefinition> modulesToMerge, ModuleDefinition mainModule)
        {
            // Stelle sicher, dass die Haupt-Assembly nicht in der Liste enthalten ist.


            // Hole oder lege den Typ "<Module>" in der Haupt-Assembly an
            //var mainModule = mainAssembly.MainModule;
            var moduleType = mainModule.Types.FirstOrDefault(t => t.Name == "<Module>");
            if (moduleType == null)
            {
                moduleType = new TypeDefinition(
                    string.Empty,
                    "<Module>",
                    TypeAttributes.NotPublic | TypeAttributes.AutoClass | TypeAttributes.AnsiClass | TypeAttributes.BeforeFieldInit,
                    mainModule.TypeSystem.Object
                );
                mainModule.Types.Add(moduleType);
            }

            // Entferne ggf. einen bereits vorhandenen Module-Initializer in der Haupt-Assembly
            var existingInitializer = moduleType.Methods.FirstOrDefault(m => m.Name == ".cctor");
            if (existingInitializer != null)
            {
                moduleType.Methods.Remove(existingInitializer);
            }

            // Erzeuge den neuen Module-Initializer: static, parameterlos, void
            var newInitializer = new MethodDefinition(
                ".cctor",
                MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                mainModule.TypeSystem.Void
            );

            // Füge das ModuleInitializer-Attribut hinzu – falls noch nicht vorhanden, legen wir es an
            //var moduleInitializerAttrCtor = mainModule.ImportReference(typeof(System.Runtime.CompilerServices.ModuleInitializerAttribute).GetConstructor(Type.EmptyTypes));
            //newInitializer.CustomAttributes.Add(new CustomAttribute(moduleInitializerAttrCtor));

            // Erstelle den Methodenkörper
            var il = newInitializer.Body.GetILProcessor();

            // Für jede zusätzliche Assembly: suche die statische "<Module>"-Methode und füge einen Aufruf ein
            foreach (var module in modulesToMerge)
            {
                // Suche in der Assembly den Typ "<Module>"
                var asmModuleType = module.Types.FirstOrDefault(t => t.Name == "<Module>");
                if (asmModuleType == null)
                    continue;

                // Suche die Methode "<Module>" (die Module-Initializer-Methode)
                var asmInitializer = asmModuleType.Methods.FirstOrDefault(m => m.Name == ".cctor" && m.IsStatic);
                if (asmInitializer == null)
                    continue;

                var body = asmInitializer.Body;
                var map = new Dictionary<Instruction, Instruction>();

                foreach (var instruction in body.Instructions)
                {
                    Instruction newInstruction;// = Instruction.Create(instruction.OpCode);
                    //newInstruction.Operand = instruction.Operand;
                    if (instruction.Operand is MethodReference methodRef)
                    {
                        newInstruction = Instruction.Create(instruction.OpCode, mainModule.ImportReference(methodRef));
                        //newInstruction.Operand = mainModule.ImportReference(methodRef);
                    }
                    else if (instruction.Operand is FieldReference fieldRef)
                    {
                        newInstruction = Instruction.Create(instruction.OpCode, mainModule.ImportReference(fieldRef));
                        //newInstruction.Operand = mainModule.ImportReference(fieldRef);
                    }
                    else if (instruction.Operand is TypeReference typeRef)
                    {
                        newInstruction = Instruction.Create(instruction.OpCode, mainModule.ImportReference(typeRef));
                        //newInstruction.Operand = mainModule.ImportReference(typeRef);
                    }
                    else if (instruction.OpCode == OpCodes.Ret)
                    {
                        // skip
                        continue;
                    }
                    else
                    {
                        throw new NotImplementedException();
                    }
                    map[instruction] = newInstruction;
                }

                foreach (var pair in map)
                {
                    if (pair.Key.Operand is Instruction targetInstruction)
                    {
                        pair.Value.Operand = map[targetInstruction];
                    }
                    il.Append(pair.Value);
                }

                asmModuleType.Methods.Remove(asmInitializer);

                //// Importiere die Methode in den Kontext der Haupt-Assembly
                //var importedMethod = mainModule.ImportReference(asmInitializer);

                //// Füge einen Call-Aufruf zum IL-Stream hinzu
                //il.Append(il.Create(OpCodes.Call, importedMethod));
            }

            // Füge ein Ret (Return) hinzu
            il.Append(il.Create(OpCodes.Ret));

            // Füge den neuen Module-Initializer der "<Module>"-Klasse der Haupt-Assembly hinzu
            moduleType.Methods.Add(newInitializer);
        }

        /// <summary>
        /// Sucht im Modul nach dem Typ "ModuleInitializerAttribute" und gibt die Parameterlose Konstruktorreferenz zurück.
        /// Falls der Typ nicht existiert, wird er angelegt.
        /// </summary>
        /// <param name="module">Das ModuleDefinition, in dem gesucht bzw. angelegt wird.</param>
        /// <returns>Eine MethodReference auf den parameterlosen Konstruktor des ModuleInitializerAttribute.</returns>
        static MethodReference GetModuleInitializerAttributeConstructor(ModuleDefinition module)
        {
            // Sucht in den Typen des Moduls
            var attrType = module.Types.FirstOrDefault(t => t.Name == "ModuleInitializerAttribute");
            if (attrType == null)
            {
                // Lege den Typ "ModuleInitializerAttribute" unter dem Namespace "System.Runtime.CompilerServices" an
                attrType = new TypeDefinition(
                    "System.Runtime.CompilerServices",
                    "ModuleInitializerAttribute",
                    TypeAttributes.Public | TypeAttributes.Sealed,
                    module.ImportReference(typeof(System.Attribute))
                );
                module.Types.Add(attrType);

                // Erzeuge den parameterlosen Konstruktor
                var ctor = new MethodDefinition(
                    ".ctor",
                    MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                    module.TypeSystem.Void
                );

                // IL: ldarg.0 -> call instance void [mscorlib]System.Object::.ctor() -> ret
                var il = ctor.Body.GetILProcessor();
                il.Append(il.Create(OpCodes.Ldarg_0));
                var objectCtor = module.ImportReference(typeof(object).GetConstructor(new Type[0]));
                il.Append(il.Create(OpCodes.Call, objectCtor));
                il.Append(il.Create(OpCodes.Ret));

                attrType.Methods.Add(ctor);
                return ctor;
            }
            else
            {
                // Falls der Typ bereits existiert, suche den parameterlosen Konstruktor
                var ctor = attrType.Methods.FirstOrDefault(m => m.IsConstructor && !m.HasParameters);
                if (ctor != null)
                    return module.ImportReference(ctor);
            }

            return null;


        }


        public void Perform()
        {
            RepackTypes();
            RepackExportedTypes();
        }

        private void RepackTypes()
        {
            _logger.Verbose("Processing types");

            // merge types, this differs between 'primary' and 'other' assemblies regarding internalizing

            var otherModules = _repackContext.OtherAssemblies.SelectMany(x => x.Modules).ToArray();
            MergeModuleInitializers(otherModules, _repackContext.TargetAssemblyMainModule);

            foreach (var r in _repackContext.PrimaryAssemblyDefinition.Modules.SelectMany(x => x.Types))
            {
                _logger.Verbose($"- Importing {r} from {r.Module}");
                _repackImporter.Import(r, _repackContext.TargetAssemblyMainModule.Types, false);
            }

            foreach (var module in _repackContext.OtherAssemblies.SelectMany(x => x.Modules))
            {
                bool internalizeAssembly = ShouldInternalizeAssembly(module.Assembly.Name.Name);
                foreach (var r in module.Types)
                {
                    _logger.Verbose($"- Importing {r} from {r.Module}");
                    _repackImporter.Import(r, _repackContext.TargetAssemblyMainModule.Types, ShouldInternalize(r, internalizeAssembly));
                }
            }
        }

        private bool IsTypeForwarder(ExportedType exportedType)
        {
            if (exportedType.IsForwarder)
            {
                return true;
            }

            if (exportedType.DeclaringType is { } declaringType)
            {
                return IsTypeForwarder(declaringType);
            }

            return false;
        }

        private void RepackExportedTypes()
        {
            var targetAssemblyMainModule = _repackContext.TargetAssemblyMainModule;
            _logger.Verbose("Processing exported types");

            foreach (var module in _repackContext.MergedAssemblies.SelectMany(x => x.Modules))
            {
                bool isPrimaryAssembly = module.Assembly == _repackContext.PrimaryAssemblyDefinition;
                bool internalizeAssembly = !isPrimaryAssembly && ShouldInternalizeAssembly(module.Assembly.Name.Name);

                foreach (var exportedType in module.ExportedTypes)
                {
                    TypeReference reference = null;
                    bool forwardedToTargetAssembly = false;

                    if (IsTypeForwarder(exportedType))
                    {
                        var forwardedTo = _allTypes.FirstOrDefault(t => t.FullName == exportedType.FullName);
                        if (forwardedTo != null)
                        {
                            forwardedToTargetAssembly = true;
                            reference = forwardedTo;
                        }
                    }

                    if (reference == null)
                    {
                        reference = CreateReference(exportedType);
                    }

                    _repackContext.MappingHandler.StoreExportedType(
                        module,
                        exportedType.FullName,
                        reference);

                    if (ShouldInternalize(exportedType.FullName, internalizeAssembly))
                    {
                        continue;
                    }

                    if (forwardedToTargetAssembly)
                    {
                        continue;
                    }

                    _logger.Verbose($"- Importing Exported Type {exportedType} from {exportedType.Scope}");
                    _repackImporter.Import(
                        exportedType,
                        targetAssemblyMainModule.ExportedTypes,
                        targetAssemblyMainModule);
                }
            }
        }

        private bool ShouldInternalizeAssembly(string assemblyShortName)
        {
            bool internalizeAssembly = _repackOptions.InternalizeAssemblies.Contains(assemblyShortName, StringComparer.OrdinalIgnoreCase);

            if (!_repackOptions.Internalize && !internalizeAssembly)
            {
                return false;
            }

            if (_repackOptions.ExcludeInternalizeAssemblies.Contains(assemblyShortName, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Check if a type's FullName matches a Regex to exclude it from internalizing.
        /// </summary>
        private bool ShouldInternalize(string typeFullName, bool internalizeAssembly)
        {
            if (!internalizeAssembly)
            {
                return false;
            }

            if (_repackOptions.ExcludeInternalizeMatches.Count == 0)
            {
                return true;
            }

            string withSquareBrackets = "[" + typeFullName + "]";
            foreach (Regex r in _repackOptions.ExcludeInternalizeMatches)
            {
                if (r.IsMatch(typeFullName) || r.IsMatch(withSquareBrackets))
                {
                    return false;
                }
            }

            return true;
        }

        private bool ShouldInternalize(TypeDefinition type, bool internalizeAssembly)
        {
            if (!internalizeAssembly)
            {
                return false;
            }

            if (_repackOptions.ExcludeInternalizeSerializable && IsSerializableAndPublic(type))
            {
                return false;
            }

            return ShouldInternalize(type.FullName, internalizeAssembly);
        }

        private bool IsSerializableAndPublic(TypeDefinition type)
        {
            if (!type.IsPublic && !type.IsNestedPublic) return false;

            if (type.Attributes.HasFlag(TypeAttributes.Serializable))
                return true;

            if (type.HasCustomAttributes && type.CustomAttributes.Any(IsSerializable))
            {
                return true;
            }

            return type.HasNestedTypes && type.NestedTypes.Any(IsSerializableAndPublic);
        }

        private bool IsSerializable(CustomAttribute attribute)
        {
            var name = attribute.AttributeType.FullName;
            return name == "System.Runtime.Serialization.DataContractAttribute" ||
                   name == "System.ServiceModel.ServiceContractAttribute" ||
                   name == "System.Xml.Serialization.XmlRootAttribute" ||
                   name == "System.Xml.Serialization.XmlTypeAttribute";
        }

        private TypeReference CreateReference(ExportedType type)
        {
            return new TypeReference(type.Namespace, type.Name, _repackContext.TargetAssemblyMainModule, _repackContext.MergeScope(type.Scope))
            {
                DeclaringType = type.DeclaringType != null ? CreateReference(type.DeclaringType) : null,
            };
        }
    }
}
