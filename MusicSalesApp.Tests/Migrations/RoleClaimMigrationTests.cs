using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MusicSalesApp.Migrations;

namespace MusicSalesApp.Tests.Migrations;

[TestFixture]
public class RoleClaimMigrationTests
{
    [Test]
    public void AddManageAllCreatorSongsPermission_UsesLogicalIdempotentInsertWithoutFixedPrimaryKey()
    {
        var operation = GetOnlySqlOperation(new TestableMigration().BuildUp());

        Assert.Multiple(() =>
        {
            Assert.That(operation.Sql, Does.Contain("IF NOT EXISTS"));
            Assert.That(operation.Sql, Does.Contain("[ClaimValue] = N'ManageAllCreatorSongs'"));
            Assert.That(operation.Sql, Does.Contain(
                "INSERT INTO [AspNetRoleClaims] ([RoleId], [ClaimType], [ClaimValue])"));
            Assert.That(operation.Sql, Does.Not.Contain("[Id]"));
        });
    }

    [Test]
    public void AddManageAllCreatorSongsPermission_DownDeletesByLogicalIdentity()
    {
        var operation = GetOnlySqlOperation(new TestableMigration().BuildDown());

        Assert.Multiple(() =>
        {
            Assert.That(operation.Sql, Does.Contain("DELETE FROM [AspNetRoleClaims]"));
            Assert.That(operation.Sql, Does.Contain("[RoleId] = 1"));
            Assert.That(operation.Sql, Does.Contain("[ClaimType] = N'Permission'"));
            Assert.That(operation.Sql, Does.Contain("[ClaimValue] = N'ManageAllCreatorSongs'"));
            Assert.That(operation.Sql, Does.Not.Contain("[Id]"));
        });
    }

    private static SqlOperation GetOnlySqlOperation(IReadOnlyList<MigrationOperation> operations)
    {
        Assert.That(operations, Has.Count.EqualTo(1));
        return (SqlOperation)operations.Single();
    }

    private sealed class TestableMigration : AddManageAllCreatorSongsPermission
    {
        public IReadOnlyList<MigrationOperation> BuildUp()
        {
            var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
            base.Up(builder);
            return builder.Operations;
        }

        public IReadOnlyList<MigrationOperation> BuildDown()
        {
            var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
            base.Down(builder);
            return builder.Operations;
        }
    }
}
