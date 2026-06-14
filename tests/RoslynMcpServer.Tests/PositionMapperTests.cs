using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

using RoslynMcpServer.Core.Analysis;

using Xunit;

namespace RoslynMcpServer.Tests;

/// <summary>
/// Tests for the PositionMapper — the 1-based → 0-based conversion that every
/// semantic tool relies on. A bug here breaks hover, goto_definition,
/// find_references, and completion.
/// </summary>
public class PositionMapperTests
{
    [Fact]
    public void ToLinePosition_Converts_1Based_To_0Based()
    {
        var source = SourceText.From("var x = 42;\nvar y = x;\n");

        var pos = PositionMapper.ToLinePosition(source, line1Based: 2, column1Based: 10, symbolText: "x");

        Assert.Equal(1, pos.Line);  // 0-based
        Assert.Equal(9, pos.Character); // 0-based
    }

    [Fact]
    public void ToLinePosition_Locates_Symbol_On_Line()
    {
        var source = SourceText.From("Console.WriteLine(\"hello\");\n");

        var pos = PositionMapper.ToLinePosition(source, line1Based: 1, column1Based: 0, symbolText: "WriteLine");

        // "Console." is 8 chars, so "WriteLine" starts at column 8 (0-based).
        Assert.Equal(0, pos.Line);
        Assert.Equal(8, pos.Character);
    }

    [Fact]
    public void ToLinePosition_Falls_Back_To_Column_Zero_When_Symbol_Not_Found()
    {
        var source = SourceText.From("var x = 1;\n");

        var pos = PositionMapper.ToLinePosition(source, line1Based: 1, column1Based: 0, symbolText: "nonexistent");

        Assert.Equal(0, pos.Line);
        Assert.Equal(0, pos.Character);
    }

    [Fact]
    public void ReadLine_Returns_Line_Content()
    {
        var file = Path.Combine(Path.GetTempPath(), $"pm_test_{Guid.NewGuid():N}.cs");
        File.WriteAllText(file, "line one\nline two\nline three\n");

        try
        {
            Assert.Equal("line two", PositionMapper.ReadLine(file, 2));
            Assert.Equal("line three", PositionMapper.ReadLine(file, 3));
            Assert.Equal("", PositionMapper.ReadLine(file, 99)); // out of range
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ReadLine_Nonexistent_File_Returns_Empty()
    {
        Assert.Equal("", PositionMapper.ReadLine("/nonexistent/file.cs", 1));
    }
}
