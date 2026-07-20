using Caffeinated.NetworkTesting.Editor.PackageControl;
using NUnit.Framework;

namespace Caffeinated.NetworkTesting.Tests
{
    public sealed class WindowsCommandLineTests
    {
        [TestCase("", "\"\"")]
        [TestCase("plain", "\"plain\"")]
        [TestCase("two words", "\"two words\"")]
        [TestCase("a\"b", "\"a\\\"b\"")]
        [TestCase("C:\\Some Path\\", "\"C:\\Some Path\\\\\"")]
        public void QuoteArgument_ProducesWindowsCreateProcessSafeArgument(
            string argument,
            string expected)
        {
            Assert.That(WindowsCommandLine.QuoteArgument(argument), Is.EqualTo(expected));
        }

        [Test]
        public void JoinArguments_PreservesArgumentBoundaries()
        {
            string[] arguments =
            {
                "-File",
                @"C:\Package Root\Tools~\Run.ps1",
                "-ProjectPath",
                @"C:\Consumer Project"
            };

            string commandLine = WindowsCommandLine.JoinArguments(arguments);

            Assert.That(
                commandLine,
                Is.EqualTo(
                    "\"-File\" \"C:\\Package Root\\Tools~\\Run.ps1\" " +
                    "\"-ProjectPath\" \"C:\\Consumer Project\""));
        }
    }
}
