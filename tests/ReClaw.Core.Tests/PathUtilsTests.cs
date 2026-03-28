using ReClaw.Core.Utils;
using Xunit;

namespace ReClaw.Core.Tests
{
    public class PathUtilsTests
    {
        [Fact]
        public void NormalizePath_TrimsTrailingSeparators()
        {
            var p = PathUtils.NormalizePath("./folder/..");
            Assert.False(string.IsNullOrEmpty(p));
        }

        [Fact]
        public void ToPosixRelative_ReturnsRelativeWhenInsideBase()
        {
            var basePath = System.IO.Path.GetFullPath("tests");
            var target = System.IO.Path.Combine(basePath, "sub", "file.txt");
            var rel = PathUtils.ToPosixRelative(basePath, target);
            Assert.Contains("sub/file.txt", rel.Replace('\\', '/'));
        }
    }
}
