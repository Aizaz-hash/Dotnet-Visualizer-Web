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

        using var stream = file.OpenReadStream();
        using var peReader = new PEReader(stream);

        if (!peReader.HasMetadata)
        {
            throw new InvalidDataException("The uploaded file does not contain valid .NET assembly metadata.");
        }

        MetadataReader mdReader = peReader.GetMetadataReader();

        // --- FIRST PASS: Map out Classes, Interfaces, and all declared Methods ---
        foreach (TypeDefinitionHandle typeHandle in mdReader.TypeDefinitions)
        {
            try
            {
                TypeDefinition typeDef = mdReader.GetTypeDefinition(typeHandle);

                string name = mdReader.GetString(typeDef.Name);
                string ns = mdReader.GetString(typeDef.Namespace);
                string fullName = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";

                if (name.StartsWith("<") || name.Equals("<Module>")) continue;

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
                    catch { /* Resilient to missing reference signatures */ }
                }

                // Register Methods & avoid Overload collisions
                if (!isInterface)
                {
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
                        }
                        catch {  }
                    }
                }
            }
            catch { }
        }

        // --- SECOND PASS: Scan IL Byte Streams for Cross-Method References ---
        // --- SECOND PASS: Scan IL Byte Streams for Cross-Method References ---
        foreach (TypeDefinitionHandle typeHandle in mdReader.TypeDefinitions)
        {
            try
            {
                TypeDefinition typeDef = mdReader.GetTypeDefinition(typeHandle);
                string name = mdReader.GetString(typeDef.Name);
                if (name.StartsWith("<") || name.Equals("<Module>")) continue;

                bool isInterface = (typeDef.Attributes & TypeAttributes.Interface) != 0;
                if (isInterface) continue;

                foreach (MethodDefinitionHandle methodHandle in typeDef.GetMethods())
                {
                    if (!tokenToUniqueMethodIdMap.TryGetValue(methodHandle, out string? callerMethodId)) continue;

                    MethodDefinition methodDef = mdReader.GetMethodDefinition(methodHandle);
                    if (methodDef.RelativeVirtualAddress == 0) continue;

                    try
                    {
                        MethodBodyBlock body = peReader.GetMethodBody(methodDef.RelativeVirtualAddress);
                        byte[] ilBytes = body.GetILBytes();
                        int blobIndex = 0;

                        while (blobIndex < ilBytes.Length)
                        {
                            byte opCode = ilBytes[blobIndex];
                            blobIndex++;

                            if (opCode == 0x28 || opCode == 0x6F) // Call or Callvirt
                            {
                                if (blobIndex + 4 <= ilBytes.Length)
                                {
                                    int tokenVal = BitConverter.ToInt32(ilBytes, blobIndex);
                                    blobIndex += 4;

                                    EntityHandle calledEntityHandle = MetadataTokens.EntityHandle(tokenVal);
                                    string calleeMethodId = string.Empty;
                                    string calleeParentClass = string.Empty;

                                    if (calledEntityHandle.Kind == HandleKind.MethodDefinition)
                                    {
                                        if (tokenToUniqueMethodIdMap.TryGetValue(calledEntityHandle, out string? matchedId))
                                        {
                                            calleeMethodId = matchedId;
                                            // Extract parent class from the registered method definition node if needed
                                        }
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
                                                // Attempt to look up if this refers to an internal method we mapped
                                                string lookupBase = $"{calleeParentClass}.{targetMethodName}()";

                                                // Simple lookup fallback fallback matching registered keys
                                                calleeMethodId = lookupBase;
                                            }
                                        }
                                    }

                                    if (!string.IsNullOrEmpty(calleeMethodId) && !calleeMethodId.Equals(callerMethodId))
                                    {
                                        // We include the explicit caller parent class and target parent class in the edge data
                                        string callerParent = tokenToUniqueMethodIdMap[methodHandle].Split('(')[0];
                                        int lastDot = callerParent.LastIndexOf('.');
                                        callerParent = lastDot > -1 ? callerParent.Substring(0, lastDot) : callerParent;

                                        methodEdges.Add(new
                                        {
                                            from = callerMethodId,
                                            to = calleeMethodId,
                                            relation = "Calls",
                                            fromClass = callerParent,
                                            toClass = calleeParentClass
                                        });
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Suppress invalid IL headers safely
                    }
                }
            }
            catch { /* Skip scope if metadata bounds drift */ }
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
                var ts = reader.GetTypeSpecification((TypeSpecificationHandle)handle);
                // Safe fallback string to gracefully bypass nested structural components instead of throwing
                return "GenericTypeSpecification";
            }
        }
        catch { }
        return string.Empty;
    }
}