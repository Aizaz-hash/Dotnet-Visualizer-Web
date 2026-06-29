using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAntiforgery();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapPost("/api/analyze-dll", async (IFormFile file) =>
{
    if (file == null || file.Length == 0)
        return Results.BadRequest("No file uploaded.");

    try
    {
        var nodes = new List<object>();
        var edges = new List<object>();
        var classList = new List<string>();

        // Open a direct stream to read raw binary metadata tables instantly
        using var stream = file.OpenReadStream();
        using var peReader = new PEReader(stream);

        if (!peReader.HasMetadata)
        {
            return Results.BadRequest("The uploaded file does not contain valid .NET assembly metadata tables.");
        }

        MetadataReader mdReader = peReader.GetMetadataReader();

        // Scan and parse type definitions without running runtime layout verification checks
        foreach (TypeDefinitionHandle typeHandle in mdReader.TypeDefinitions)
        {
            TypeDefinition typeDef = mdReader.GetTypeDefinition(typeHandle);

            // Extract Name and Namespace strings safely
            string name = mdReader.GetString(typeDef.Name);
            string ns = mdReader.GetString(typeDef.Namespace);
            string fullName = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";

            // Skip compiler-generated structures (<Module>, anonymous type closures, etc.)
            if (name.StartsWith("<") || name.Equals("<Module>")) continue;

            // STALWART FILTER: Only inspect architectures containing your target keyword
            if (!fullName.Contains("Alachisoft", StringComparison.OrdinalIgnoreCase)) continue;

            // Identify whether the token signature represents an Interface or a Class
            bool isInterface = (typeDef.Attributes & System.Reflection.TypeAttributes.Interface) != 0;
            classList.Add(fullName);

            nodes.Add(new
            {
                Id = fullName,
                Name = name,
                Type = isInterface ? "Interface" : "Class"
            });

            // Trace Base Class Inheritance paths safely using token names
            if (!typeDef.BaseType.IsNil)
            {
                string baseName = GetStringFromEntityHandle(mdReader, typeDef.BaseType);
                if (!string.IsNullOrEmpty(baseName) && !baseName.Equals("System.Object") &&
                    baseName.Contains("Alachisoft", StringComparison.OrdinalIgnoreCase))
                {
                    edges.Add(new { From = fullName, To = baseName });
                }
            }

            // Trace Interfaces safely using token names
            foreach (InterfaceImplementationHandle ifaceHandle in typeDef.GetInterfaceImplementations())
            {
                InterfaceImplementation ifaceImpl = mdReader.GetInterfaceImplementation(ifaceHandle);
                string ifaceName = GetStringFromEntityHandle(mdReader, ifaceImpl.Interface);

                if (!string.IsNullOrEmpty(ifaceName) && ifaceName.Contains("Alachisoft", StringComparison.OrdinalIgnoreCase))
                {
                    edges.Add(new { From = fullName, To = ifaceName });
                }
            }
        }

        return Results.Ok(new { Classes = classList, Nodes = nodes, Edges = edges });
    }
    catch (Exception ex)
    {
        return Results.BadRequest($"Failed to process raw DLL binary metadata stream: {ex.Message}");
    }
})
.DisableAntiforgery();

app.Run();

// Helper method to reconstruct clean type strings from high-performance metadata tokens
static string GetStringFromEntityHandle(MetadataReader reader, EntityHandle handle)
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
            // Bypasses complex multi-nested generic types safely to avoid stream corruption
            return "GenericSpecification";
        }
    }
    catch { /* Silently bypass nested resolution outliers to keep scanning stable */ }
    return string.Empty;
}