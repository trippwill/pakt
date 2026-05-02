namespace Pakt.Core.Test;

public class TestDataSmokeTests
{
    private static string TestDataDir =>
        Path.Combine(AppContext.BaseDirectory, "TestData");

    [Fact]
    public void TestData_ValidDirectory_Exists()
    {
        string validDir = Path.Combine(TestDataDir, "valid");
        Assert.True(Directory.Exists(validDir), $"Expected testdata/valid at: {validDir}");
    }

    [Fact]
    public void TestData_InvalidDirectory_Exists()
    {
        string invalidDir = Path.Combine(TestDataDir, "invalid");
        Assert.True(Directory.Exists(invalidDir), $"Expected testdata/invalid at: {invalidDir}");
    }

    [Fact]
    public void TestData_ValidDirectory_ContainsPaktFiles()
    {
        string validDir = Path.Combine(TestDataDir, "valid");
        string[] paktFiles = Directory.GetFiles(validDir, "*.pakt");
        Assert.NotEmpty(paktFiles);
    }
}