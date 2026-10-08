using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AISupportOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEvaluationAndFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "feedback_at",
                table: "messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "feedback_comment",
                table: "messages",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "feedback_helpful",
                table: "messages",
                type: "boolean",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "evaluation_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    prompt_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    chat_model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    embedding_model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    used_judge = table.Column<bool>(type: "boolean", nullable: false),
                    started_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    summary = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_evaluation_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_evaluation_runs_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "evaluation_results",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    question = table.Column<string>(type: "text", nullable: false),
                    expected_source = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    should_abstain = table.Column<bool>(type: "boolean", nullable: false),
                    answer = table.Column<string>(type: "text", nullable: false),
                    outcome = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    retrieved_sources = table.Column<List<string>>(type: "text[]", nullable: false),
                    cited_sources = table.Column<List<string>>(type: "text[]", nullable: false),
                    expected_source_rank = table.Column<int>(type: "integer", nullable: true),
                    cited_expected_source = table.Column<bool>(type: "boolean", nullable: true),
                    key_fact_coverage = table.Column<double>(type: "double precision", nullable: true),
                    missing_key_facts = table.Column<List<string>>(type: "text[]", nullable: false),
                    abstention_correct = table.Column<bool>(type: "boolean", nullable: false),
                    groundedness = table.Column<double>(type: "double precision", nullable: true),
                    judge_notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    latency_ms = table.Column<long>(type: "bigint", nullable: false),
                    input_tokens = table.Column<long>(type: "bigint", nullable: true),
                    output_tokens = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_evaluation_results", x => x.id);
                    table.ForeignKey(
                        name: "fk_evaluation_results_evaluation_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "evaluation_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_evaluation_results_run_id",
                table: "evaluation_results",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_evaluation_results_tenant_id_run_id",
                table: "evaluation_results",
                columns: new[] { "tenant_id", "run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_evaluation_runs_tenant_id_created_at",
                table: "evaluation_runs",
                columns: new[] { "tenant_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "evaluation_results");

            migrationBuilder.DropTable(
                name: "evaluation_runs");

            migrationBuilder.DropColumn(
                name: "feedback_at",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "feedback_comment",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "feedback_helpful",
                table: "messages");
        }
    }
}
