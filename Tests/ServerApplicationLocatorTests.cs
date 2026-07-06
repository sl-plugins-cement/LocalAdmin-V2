using System;
using LocalAdmin.V2.IO;
using Xunit;

namespace LocalAdmin.V2.Tests;

public sealed class ServerApplicationLocatorTests
{
    [Fact]
    public void Empty_Or_Null_Yields_Empty()
    {
        Assert.Equal(string.Empty, ServerApplicationLocator.ExpandPath(null));
        Assert.Equal(string.Empty, ServerApplicationLocator.ExpandPath("   "));
    }

    [Fact]
    public void Strips_Surrounding_Quotes()
    {
        Assert.Equal("plain/path", ServerApplicationLocator.ExpandPath("\"plain/path\""));
    }

    [Fact]
    public void Expands_Environment_Variables()
    {
        var name = "LA_TEST_EXPAND_VAR";
        Environment.SetEnvironmentVariable(name, "resolved");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Windows %VAR% form.
                Assert.Equal("resolved/x", ServerApplicationLocator.ExpandPath("%LA_TEST_EXPAND_VAR%/x"));
                // Unix $VAR is left literal on Windows.
                Assert.Equal("$LA_TEST_EXPAND_VAR/x", ServerApplicationLocator.ExpandPath("$LA_TEST_EXPAND_VAR/x"));
            }
            else
            {
                // Unix $VAR and ${VAR} forms are expanded on non-Windows.
                Assert.Equal("resolved/x", ServerApplicationLocator.ExpandPath("$LA_TEST_EXPAND_VAR/x"));
                Assert.Equal("resolved/x", ServerApplicationLocator.ExpandPath("${LA_TEST_EXPAND_VAR}/x"));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void Leaves_Unknown_Variable_Literal()
    {
        const string input = "$LA_DEFINITELY_UNSET_VAR_XYZ/x";
        var result = ServerApplicationLocator.ExpandPath(input);

        if (OperatingSystem.IsWindows())
            Assert.Equal(input, result);           // $VAR not a Windows token
        else
            Assert.Equal(input, result);           // unknown var kept literal
    }
}
