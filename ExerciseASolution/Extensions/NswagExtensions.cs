using Namotion.Reflection;
using NSwag.Generation.Processors;
using NSwag.Generation.Processors.Contexts;
using WebSocketBoilerplate;

public sealed class AddAllDerivedTypesProcessor : IDocumentProcessor
{
    public void Process(DocumentProcessorContext context)
    {
        // Get all assemblies in the current application domain
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();

        // Find all types that inherit from BaseDto
        var derivedTypes = assemblies
            .SelectMany(a =>
            {
                try
                {
                    return a.GetTypes();
                }
                catch
                {
                    return Array.Empty<Type>();
                }
            })
            .Where(t =>
                t != typeof(BaseDto) && // Exclude BaseDto itself
                !t.IsAbstract && // Exclude abstract classes
                typeof(BaseDto).IsAssignableFrom(t)) // Check if type inherits from BaseDto
            .ToList();

        // Generate schema for each derived type
        foreach (var type in derivedTypes)
        {
            context.SchemaGenerator.Generate(type.ToContextualType(), context.SchemaResolver);
        }
    }
}