using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSalesApp.Migrations
{
    /// <inheritdoc />
    public partial class AddCreatorAgreementAcceptance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF COL_LENGTH(N'dbo.Creators', N'CreatorAgreementAccepted') IS NULL
                BEGIN
                    ALTER TABLE [dbo].[Creators]
                    ADD [CreatorAgreementAccepted] bit NOT NULL
                        CONSTRAINT [DF_Creators_CreatorAgreementAccepted] DEFAULT CAST(0 AS bit) WITH VALUES;
                END;

                IF COL_LENGTH(N'dbo.Creators', N'CreatorAgreementAcceptedAtUtc') IS NULL
                BEGIN
                    ALTER TABLE [dbo].[Creators]
                    ADD [CreatorAgreementAcceptedAtUtc] datetime2 NULL;
                END;

                EXEC(N'
                    UPDATE [dbo].[Creators]
                    SET [CreatorAgreementAccepted] = CAST(1 AS bit),
                        [CreatorAgreementAcceptedAtUtc] = COALESCE([AcknowledgmentDateTimeUtc], [PayoutRequirementsAcknowledgedAtUtc], [OnboardedAt], [UpdatedAt], [CreatedAt])
                    WHERE [IsActive] = CAST(1 AS bit)
                       OR [OnboardingStatus] = 3
                       OR [OnboardedAt] IS NOT NULL;
                ');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF COL_LENGTH(N'dbo.Creators', N'CreatorAgreementAcceptedAtUtc') IS NOT NULL
                BEGIN
                    ALTER TABLE [dbo].[Creators] DROP COLUMN [CreatorAgreementAcceptedAtUtc];
                END;

                IF COL_LENGTH(N'dbo.Creators', N'CreatorAgreementAccepted') IS NOT NULL
                BEGIN
                    DECLARE @defaultConstraintName sysname;

                    SELECT @defaultConstraintName = [dc].[name]
                    FROM [sys].[default_constraints] AS [dc]
                    INNER JOIN [sys].[columns] AS [c]
                        ON [c].[default_object_id] = [dc].[object_id]
                    WHERE [dc].[parent_object_id] = OBJECT_ID(N'dbo.Creators')
                        AND [c].[name] = N'CreatorAgreementAccepted';

                    IF @defaultConstraintName IS NOT NULL
                    BEGIN
                        EXEC(N'ALTER TABLE [dbo].[Creators] DROP CONSTRAINT ' + QUOTENAME(@defaultConstraintName));
                    END;

                    ALTER TABLE [dbo].[Creators] DROP COLUMN [CreatorAgreementAccepted];
                END;
                """);
        }
    }
}
