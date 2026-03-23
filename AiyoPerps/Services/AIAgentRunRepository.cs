using AiyoPerps.Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AiyoPerps.Services;

public sealed class AIAgentRunRepository
{
    public IReadOnlyList<AIAgentRunRecord> ListRecent(int count = 200)
    {
        DbSchemaBootstrapper.EnsureSchema();
        using var db = new AppDbContext();
        return db.AIAgentRuns
            .AsNoTracking()
            .ToList()
            .OrderByDescending(x => x.StartedAt)
            .Take(Math.Max(1, count))
            .Select(ToRecord)
            .ToList();
    }

    public AIAgentRunRecord? Find(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return null;
        }

        DbSchemaBootstrapper.EnsureSchema();
        using var db = new AppDbContext();
        var entity = db.AIAgentRuns.AsNoTracking().SingleOrDefault(x => x.RunId == runId.Trim());
        return entity is null ? null : ToRecord(entity);
    }

    public void Upsert(AIAgentRunRecord record)
    {
        DbSchemaBootstrapper.EnsureSchema();
        using var db = new AppDbContext();
        var entity = db.AIAgentRuns.SingleOrDefault(x => x.RunId == record.RunId);
        if (entity is null)
        {
            db.AIAgentRuns.Add(ToEntity(record));
        }
        else
        {
            Apply(entity, record);
        }

        db.SaveChanges();
        TrimExcessRecords(db);
    }

    public void Delete(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        DbSchemaBootstrapper.EnsureSchema();
        using var db = new AppDbContext();
        var entity = db.AIAgentRuns.SingleOrDefault(x => x.RunId == runId.Trim());
        if (entity is null)
        {
            return;
        }

        db.AIAgentRuns.Remove(entity);
        db.SaveChanges();
    }

    public void Clear()
    {
        DbSchemaBootstrapper.EnsureSchema();
        using var db = new AppDbContext();
        var entities = db.AIAgentRuns.ToList();
        if (entities.Count == 0)
        {
            return;
        }

        db.AIAgentRuns.RemoveRange(entities);
        db.SaveChanges();
    }

    public void MarkDanglingRunsAsFailed(string reason)
    {
        DbSchemaBootstrapper.EnsureSchema();
        using var db = new AppDbContext();
        var dangling = db.AIAgentRuns
            .ToList();

        dangling = dangling
            .Where(x => string.Equals(x.Status, AIAgentExecutionService.StatusRunning, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (dangling.Count == 0)
        {
            return;
        }

        var finishedAt = DateTimeOffset.UtcNow;
        foreach (var item in dangling)
        {
            item.Status = AIAgentExecutionService.StatusFailed;
            item.FinishedAt = finishedAt;
            item.Stderr = string.IsNullOrWhiteSpace(item.Stderr)
                ? reason
                : $"{item.Stderr}{Environment.NewLine}{reason}";
        }

        db.SaveChanges();
    }

    private static void TrimExcessRecords(AppDbContext db)
    {
        var excess = db.AIAgentRuns
            .ToList();

        excess = excess
            .OrderByDescending(x => x.StartedAt)
            .Skip(200)
            .ToList();

        if (excess.Count == 0)
        {
            return;
        }

        db.AIAgentRuns.RemoveRange(excess);
        db.SaveChanges();
    }

    private static void Apply(AIAgentRunEntity entity, AIAgentRunRecord record)
    {
        entity.StartedAt = record.StartedAt;
        entity.FinishedAt = record.FinishedAt;
        entity.AgentType = record.AgentType;
        entity.Status = record.Status;
        entity.ExitCode = record.ExitCode;
        entity.WorkingDirectory = record.WorkingDirectory;
        entity.RenderedCommand = record.RenderedCommand;
        entity.RenderedPrompt = record.RenderedPrompt;
        entity.Stdout = record.Stdout;
        entity.Stderr = record.Stderr;
    }

    private static AIAgentRunEntity ToEntity(AIAgentRunRecord record)
    {
        return new AIAgentRunEntity
        {
            RunId = record.RunId,
            StartedAt = record.StartedAt,
            FinishedAt = record.FinishedAt,
            AgentType = record.AgentType,
            Status = record.Status,
            ExitCode = record.ExitCode,
            WorkingDirectory = record.WorkingDirectory,
            RenderedCommand = record.RenderedCommand,
            RenderedPrompt = record.RenderedPrompt,
            Stdout = record.Stdout,
            Stderr = record.Stderr
        };
    }

    private static AIAgentRunRecord ToRecord(AIAgentRunEntity entity)
    {
        return new AIAgentRunRecord(
            entity.RunId,
            entity.StartedAt,
            entity.FinishedAt,
            entity.AgentType,
            entity.Status,
            entity.ExitCode,
            entity.WorkingDirectory,
            entity.RenderedCommand,
            entity.RenderedPrompt,
            entity.Stdout,
            entity.Stderr);
    }
}
