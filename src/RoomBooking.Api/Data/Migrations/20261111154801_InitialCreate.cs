using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace RoomBooking.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Rooms",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Capacity = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rooms", x => x.Id);
                    table.CheckConstraint("CK_Rooms_Capacity", "Capacity > 0");
                    table.CheckConstraint("CK_Rooms_Name", "length(Name) BETWEEN 1 AND 100");
                });

            migrationBuilder.CreateTable(
                name: "Reservations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RoomId = table.Column<int>(type: "INTEGER", nullable: false),
                    StartDateTime = table.Column<long>(type: "INTEGER", nullable: false),
                    EndDateTime = table.Column<long>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreateDateTime = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reservations", x => x.Id);
                    table.CheckConstraint("CK_Reservations_TimeRange", "StartDateTime < EndDateTime");
                    table.CheckConstraint("CK_Reservations_Title", "length(Title) BETWEEN 1 AND 200");
                    table.ForeignKey(
                        name: "FK_Reservations_Rooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "Rooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "Rooms",
                columns: new[] { "Id", "Capacity", "Name" },
                values: new object[,]
                {
                    { 1, 4, "Small Room" },
                    { 2, 10, "Conference Room" },
                    { 3, 50, "Auditorium" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_RoomId_StartDateTime",
                table: "Reservations",
                columns: new[] { "RoomId", "StartDateTime" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Reservations");

            migrationBuilder.DropTable(
                name: "Rooms");
        }
    }
}
