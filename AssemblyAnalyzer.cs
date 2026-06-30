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

        // High-performance token/string trackers
        var registeredMethodIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tokenToUniqueMethodIdMap = new Dictionary<EntityHandle, string>();

        using var stream = file.OpenReadStream();
        using var peReader = new PEReader(stream);

        if (!peReader.HasMetadata)
        {
            throw new InvalidDataException("The uploaded file does not contain valid .NET assembly metadata tables.");
        }

        MetadataReader mdReader = peReader.GetMetadataReader();

        // --- FIRST PASS: Map out Classes and all declared Methods ---
        foreach (TypeDefinitionHandle typeHandle in mdReader.TypeDefinitions)
        {
            TypeDefinition typeDef = mdReader.GetTypeDefinition(typeHandle);

            string name = mdReader.GetString(typeDef.Name);
            string ns = mdReader.GetString(typeDef.Namespace);
            string fullName = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";

            if (name.StartsWith("<") || name.Equals("<Module>")) continue;
            if (!fullName.Contains("Alachisoft", StringComparison.OrdinalIgnoreCase)) continue;

            bool isInterface = (typeDef.Attributes & System.Reflection.TypeAttributes.Interface) != 0;
            classList.Add(fullName);

            inheritanceNodes.Add(new { id = fullName, name = name, type = isInterface ? "Interface" : "Class" });

            // Base Inheritance Strings
            if (!typeDef.BaseType.IsNil)
            {
                string baseName = GetStringFromEntityHandle(mdReader, typeDef.BaseType);
                if (!string.IsNullOrEmpty(baseName) && !baseName.Equals("System.Object") && baseName.Contains("Alachisoft", StringComparison.OrdinalIgnoreCase))
                {
                    inheritanceEdges.Add(new { from = fullName, to = baseName, relation = "Inherits" });
                }
            }

            // Interface Strings
            foreach (InterfaceImplementationHandle ifaceHandle in typeDef.GetInterfaceImplementations())
            {
                InterfaceImplementation ifaceImpl = mdReader.GetInterfaceImplementation(ifaceHandle);
                string ifaceName = GetStringFromEntityHandle(mdReader, ifaceImpl.Interface);
                if (!string.IsNullOrEmpty(ifaceName) && ifaceName.Contains("Alachisoft", StringComparison.OrdinalIgnoreCase))
                {
                    inheritanceEdges.Add(new { from = fullName, to = ifaceName, relation = "Implements" });
                }
            }

            // Register Methods & avoid Overload collisions
            if (!isInterface)
            {
                foreach (MethodDefinitionHandle methodHandle in typeDef.GetMethods())
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
            }
        }

        // --- SECOND PASS: Deep Scan IL Byte Streams for Cross-Method References ---
        foreach (TypeDefinitionHandle typeHandle in mdReader.TypeDefinitions)
        {
            TypeDefinition typeDef = mdReader.GetTypeDefinition(typeHandle);
            string ns = mdReader.GetString(typeDef.Namespace);
            string name = mdReader.GetString(typeDef.Name);
            string fullName = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";

            if (!fullName.Contains("Alachisoft", StringComparison.OrdinalIgnoreCase)) continue;
            bool isInterface = (typeDef.Attributes & System.Reflection.TypeAttributes.Interface) != 0;

            if (!isInterface)
            {
                foreach (MethodDefinitionHandle methodHandle in typeDef.GetMethods())
                {
                    if (!tokenToUniqueMethodIdMap.TryGetValue(methodHandle, out string? callerMethodId)) continue;

                    MethodDefinition methodDef = mdReader.GetMethodDefinition(methodHandle);

                    // If method has no body execution block (abstract/extern), jump out
                    if (methodDef.RelativeVirtualAddress == 0) continue;

                    try
                    {
                        MethodBodyBlock body = peReader.GetMethodBody(methodDef.RelativeVirtualAddress);
                        byte[] ilBytes = body.GetILBytes();
                        int blobIndex = 0;

                        // Scan IL stream sequentially for Call (0x28) and Callvirt (0x6F) opcodes
                        while (blobIndex < ilBytes.Length)
                        {
                            byte opCode = ilBytes[blobIndex];
                            blobIndex++;

                            if (opCode == 0x28 || opCode == 0x6F) // ILOpCodes.Call or Callvirt
                            {
                                if (blobIndex + 4 <= ilBytes.Length)
                                {
                                    // Extract the 4-byte metadata token integer
                                    int tokenVal = BitConverter.ToInt32(ilBytes, blobIndex);
                                    blobIndex += 4;

                                    EntityHandle calledEntityHandle = MetadataTokens.EntityHandle(tokenVal);
                                    string calleeMethodId = string.Empty;

                                    if (calledEntityHandle.Kind == HandleKind.MethodDefinition)
                                    {
                                        tokenToUniqueMethodIdMap.TryGetValue(calledEntityHandle, out calleeMethodId!);
                                    }
                                    else if (calledEntityHandle.Kind == HandleKind.MemberReference)
                                    {
                                        var memRef = mdReader.GetMemberReference((MemberReferenceHandle)calledEntityHandle);
                                        if (memRef.Parent.Kind == HandleKind.TypeReference || memRef.Parent.Kind == HandleKind.TypeDefinition)
                                        {
                                            string parentType = GetStringFromEntityHandle(mdReader, memRef.Parent);
                                            string targetMethodName = mdReader.GetString(memRef.Name);

                                            if (parentType.Contains("Alachisoft", StringComparison.OrdinalIgnoreCase) &&
                                                !targetMethodName.StartsWith("<") && !targetMethodName.Equals(".ctor"))
                                            {
                                                calleeMethodId = $"{parentType}.{targetMethodName}()";
                                            }
                                        }
                                    }

                                    // Record the invocation link: Caller -> Callee
                                    if (!string.IsNullOrEmpty(calleeMethodId) && !calleeMethodId.Equals(callerMethodId))
                                    {
                                        // Use a HashSet instead of a List for lookups
                                        var edgeSet = new HashSet<MethodEdge>();

                                        // When checking for duplicates:
                                        var newEdge = new MethodEdge(callerMethodId, calleeMethodId, "Calls");
                                        if (edgeSet.Add(newEdge)) // Add returns true only if it wasn't already there
                                        {
                                            methodEdges.Add(new { from = callerMethodId, to = calleeMethodId, relation = "Calls" });
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Safely suppress invalid IL byte parsing drops on obfuscated blocks
                    }
                }
            }
        }

        return new
        {
            classes = classList,
            inheritance = new { nodes = inheritanceNodes, edges = inheritanceEdges },
            methodGraph = new { nodes = methodNodes, edges = methodEdges }
        };
    }

    public record MethodEdge(string From, string To, string Relation);


    private static string GetStringFromEntityHandle(MetadataReader reader, EntityHandle handle)
    {
        try
        {
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
                return "GenericSpecification";
            }
        }
        catch { }
        return string.Empty;
    }
}