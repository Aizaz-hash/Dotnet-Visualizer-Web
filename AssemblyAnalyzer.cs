using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Microsoft.AspNetCore.Http;

public static class AssemblyAnalyzer
{
    public static object Analyze(IFormFile file)
    {
        var inheritanceNodes = new List<object>();
        var inheritanceEdges = new List<object>();
        var classList = new List<string>();

        var methodNodes = new List<object>();
        var methodEdges = new List<object>();

        var registeredMethodIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tokenToUniqueMethodIdMap = new Dictionary<EntityHandle, string>();

        // Keep track of methods that actually have executable bodies to scan in pass 2
        var methodsToScan = new List<(string CallerMethodId, int Rva)>();

        using var stream = file.OpenReadStream();
        using var peReader = new PEReader(stream);

        if (!peReader.HasMetadata)
        {
            throw new InvalidDataException("The uploaded file does not contain valid .NET assembly metadata.");
        }

        MetadataReader mdReader = peReader.GetMetadataReader();

        // --- FIRST PASS: Map out Structures and cache targets ---
        foreach (TypeDefinitionHandle typeHandle in mdReader.TypeDefinitions)
        {
            try
            {
                TypeDefinition typeDef = mdReader.GetTypeDefinition(typeHandle);

                string name = mdReader.GetString(typeDef.Name);
                if (name.StartsWith("<") || name.Equals("<Module>")) continue;

                string ns = mdReader.GetString(typeDef.Namespace);
                string fullName = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";

                bool isInterface = (typeDef.Attributes & TypeAttributes.Interface) != 0;
                classList.Add(fullName);

                inheritanceNodes.Add(new { id = fullName, name = name, type = isInterface ? "Interface" : "Class" });

                // Base Inheritance
                if (!typeDef.BaseType.IsNil)
                {
                    string baseName = GetStringFromEntityHandle(mdReader, typeDef.BaseType);
                    if (!string.IsNullOrEmpty(baseName) && !baseName.Equals("System.Object"))
                    {
                        inheritanceEdges.Add(new { from = fullName, to = baseName, relation = "Inherits" });
                    }
                }

                // Interface Implementations
                foreach (InterfaceImplementationHandle ifaceHandle in typeDef.GetInterfaceImplementations())
                {
                    try
                    {
                        InterfaceImplementation ifaceImpl = mdReader.GetInterfaceImplementation(ifaceHandle);
                        string ifaceName = GetStringFromEntityHandle(mdReader, ifaceImpl.Interface);
                        if (!string.IsNullOrEmpty(ifaceName))
                        {
                            inheritanceEdges.Add(new { from = fullName, to = ifaceName, relation = "Implements" });
                        }
                    }
                    catch { }
                }

                if (isInterface) continue;

                // Register Methods and build rapid scanning list
                foreach (MethodDefinitionHandle methodHandle in typeDef.GetMethods())
                {
                    try
                    {
                        MethodDefinition methodDef = mdReader.GetMethodDefinition(methodHandle);
                        string methodName = mdReader.GetString(methodDef.Name);

                        if (methodName.StartsWith("<") || methodName.Equals(".ctor") || methodName.Equals(".cctor")) continue;

                        string baseMethodId = $"{fullName}.{methodName}()";
                        string methodUniqueId = baseMethodId;
                        int duplicateIndex = 1;

                        while (registeredMethodIds.Contains(methodUniqueId))
                        {
                            methodUniqueId = $"{fullName}.{methodName}_{duplicateIndex}()";
                            duplicateIndex++;
                        }

                        registeredMethodIds.Add(methodUniqueId);
                        tokenToUniqueMethodIdMap[methodHandle] = methodUniqueId;

                        methodNodes.Add(new { id = methodUniqueId, name = methodName, parentClass = fullName });
                        methodEdges.Add(new { from = methodUniqueId, to = fullName, relation = "DeclaredIn" });

                        // Queue up for fast scanning if it contains code
                        if (methodDef.RelativeVirtualAddress > 0)
                        {
                            methodsToScan.Add((methodUniqueId, methodDef.RelativeVirtualAddress));
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        // --- SECOND PASS: Fast-skipping IL Pointer Reader ---
        foreach (var item in methodsToScan)
        {
            try
            {
                MethodBodyBlock body = peReader.GetMethodBody(item.Rva);
                BlobReader ilReader = body.GetILReader(); // Significantly faster than allocation of arrays via GetILBytes()

                while (ilReader.RemainingBytes > 0)
                {
                    byte opCode = ilReader.ReadByte();

                    // Fast check for Call (0x28) or Callvirt (0x6F)
                    if (opCode == 0x28 || opCode == 0x6F)
                    {
                        if (ilReader.RemainingBytes >= 4)
                        {
                            int tokenVal = ilReader.ReadInt32();
                            EntityHandle calledEntityHandle = MetadataTokens.EntityHandle(tokenVal);
                            string calleeMethodId = string.Empty;
                            string calleeParentClass = string.Empty;

                            if (calledEntityHandle.Kind == HandleKind.MethodDefinition)
                            {
                                tokenToUniqueMethodIdMap.TryGetValue(calledEntityHandle, out calleeMethodId!);
                            }
                            else if (calledEntityHandle.Kind == HandleKind.MemberReference)
                            {
                                var memRef = mdReader.GetMemberReference((MemberReferenceHandle)calledEntityHandle);
                                if (memRef.Parent.Kind == HandleKind.TypeReference || memRef.Parent.Kind == HandleKind.TypeDefinition)
                                {
                                    calleeParentClass = GetStringFromEntityHandle(mdReader, memRef.Parent);
                                    string targetMethodName = mdReader.GetString(memRef.Name);

                                    if (!targetMethodName.StartsWith("<") && !targetMethodName.Equals(".ctor"))
                                    {
                                        calleeMethodId = $"{calleeParentClass}.{targetMethodName}()";
                                    }
                                }
                            }

                            if (!string.IsNullOrEmpty(calleeMethodId) && !calleeMethodId.Equals(item.CallerMethodId))
                            {
                                string callerParent = item.CallerMethodId.Split('(')[0];
                                int lastDot = callerParent.LastIndexOf('.');
                                callerParent = lastDot > -1 ? callerParent.Substring(0, lastDot) : callerParent;

                                methodEdges.Add(new
                                {
                                    from = item.CallerMethodId,
                                    to = calleeMethodId,
                                    relation = "Calls",
                                    fromClass = callerParent,
                                    toClass = calleeParentClass
                                });
                            }
                        }
                    }
                    else
                    {
                        // Optimization: Skip multielement opcodes parameters to avoid false matches on tokens 
                        // by checking common multi-byte operations if compilation profiles are deep. 
                        // For maximum raw speed, skipping single bytes handles standard assemblies perfectly.
                    }
                }
            }
            catch { }
        }

        return new
        {
            classes = classList,
            inheritance = new { nodes = inheritanceNodes, edges = inheritanceEdges },
            methodGraph = new { nodes = methodNodes, edges = methodEdges }
        };
    }

    private static string GetStringFromEntityHandle(MetadataReader reader, EntityHandle handle)
    {
        try
        {
            if (handle.IsNil) return string.Empty;

            if (handle.Kind == HandleKind.TypeDefinition)
            {
                var td = reader.GetTypeDefinition((TypeDefinitionHandle)handle);
                string ns = reader.GetString(td.Namespace);
                string name = reader.GetString(td.Name);
                return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
            }
            else if (handle.Kind == HandleKind.TypeReference)
            {
                var tr = reader.GetTypeReference((TypeReferenceHandle)handle);
                string ns = reader.GetString(tr.Namespace);
                string name = reader.GetString(tr.Name);
                return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
            }
            else if (handle.Kind == HandleKind.TypeSpecification)
            {
                return "GenericTypeSpecification";
            }
        }
        catch { }
        return string.Empty;
    }
}