using System.Text;

namespace XREngine.Modeling;

public static class EditableMeshConverter
{
    public static EditableMesh ToEditable(ModelingMeshDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        ModelingMeshValidationReport report = ModelingMeshValidation.Validate(document);
        if (!report.IsValid)
            throw new InvalidOperationException(BuildValidationMessage(report));

        return new EditableMesh(document.Positions, document.TriangleIndices);
    }

    public static ModelingMeshDocument FromEditable(EditableMesh mesh, ModelingMeshMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(metadata);

        (List<System.Numerics.Vector3> vertices, List<int> indices) = mesh.Bake();
        return new ModelingMeshDocument
        {
            Positions = vertices,
            TriangleIndices = indices,
            Metadata = metadata.Clone()
        };
    }

    private static string BuildValidationMessage(ModelingMeshValidationReport report)
    {
        StringBuilder builder = new("Modeling mesh validation failed:");
        foreach (ModelingMeshValidationIssue issue in report.Issues.Where(x => x.Severity == ModelingValidationSeverity.Error))
        {
            builder.AppendLine();
            builder.Append("- ");
            builder.Append(issue.Code);
            builder.Append(": ");
            builder.Append(issue.Message);
            if (issue.ElementIndex.HasValue)
            {
                builder.Append(" (element ");
                builder.Append(issue.ElementIndex.Value);
                builder.Append(')');
            }
        }

        return builder.ToString();
    }
}
