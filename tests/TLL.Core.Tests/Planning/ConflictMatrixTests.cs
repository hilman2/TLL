using TLL.Core.Planning;
using Xunit;

namespace TLL.Core.Tests.Planning
{
    public class ConflictMatrixTests
    {
        [Theory]
        [InlineData(Relation.Compatible, Relation.Yields, Relation.Yields)]
        [InlineData(Relation.Yields, Relation.Compatible, Relation.Yields)]
        [InlineData(Relation.Yields, Relation.Yields, Relation.Yields)]
        [InlineData(Relation.Yields, Relation.HasPriority, Relation.Hard)]
        [InlineData(Relation.Compatible, Relation.Hard, Relation.Hard)]
        [InlineData(Relation.Hard, Relation.Compatible, Relation.Hard)]
        public void TightenKeepsTheStricterRelation(Relation here, Relation other, Relation expected)
        {
            var a = new ConflictMatrix(2);
            a.Set(0, 1, here);
            var b = new ConflictMatrix(2);
            b.Set(0, 1, other);

            a.Tighten(b);

            Assert.Equal(expected, a.Get(0, 1));
            // The mirrored cell must follow.
            var mirror = new ConflictMatrix(2);
            mirror.Set(0, 1, expected);
            Assert.Equal(mirror.Get(1, 0), a.Get(1, 0));
        }
    }
}
