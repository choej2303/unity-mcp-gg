using NUnit.Framework;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Infrastructure; // Access the new namespace location if applicable, or just generic Services
using System.IO;

namespace MCPForUnity.Editor.Tests
{
    public class PathResolverTests
    {
        [Test]
        public void GetPythonPath_ReturnsDefaults_WhenNoOverride()
        {
            // Arrange
            var resolver = new PathResolverService();
            
            // Act
            string pythonPath = resolver.GetPythonPath();
            
            // Assert
            Assert.IsNotNull(pythonPath);
            Assert.IsTrue(pythonPath == "python" || pythonPath == "python3" || pythonPath.EndsWith("python") || pythonPath.EndsWith("python.exe"));
        }

        [Test]
        public void GetUvxPath_ReturnsDefaultOrPath()
        {
            // Arrange
            var resolver = new PathResolverService();

            // Act
            string uvPath = resolver.GetUvxPath();

            // Assert
            Assert.IsNotNull(uvPath);
        }
    }
}
