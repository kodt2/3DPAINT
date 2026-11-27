using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LoginAndReg.Migrations
{
    /// <inheritdoc />
    public partial class UserFilesUserIdToText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_UserFiles_Users_user_id",
                table: "UserFiles");

            migrationBuilder.AlterColumn<string>(
                name: "user_id",
                table: "UserFiles",
                type: "text",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "user_id",
                table: "UserFiles",
                type: "integer",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddForeignKey(
                name: "FK_UserFiles_Users_user_id",
                table: "UserFiles",
                column: "user_id",
                principalTable: "Users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
