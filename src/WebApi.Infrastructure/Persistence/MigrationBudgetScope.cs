using System.Data;
using Microsoft.EntityFrameworkCore;

namespace WebApi.Infrastructure.Persistence;

/// <summary>Bounds one transaction without taking ownership of its connection or transaction.</summary>
public sealed class MigrationBudgetScope : IDisposable
{
    private readonly MigrationBudget budget;
    private readonly CancellationTokenSource deadline;
    private readonly CancellationTokenSource timer;
    private readonly CancellationToken externalCancellation;

    internal MigrationBudgetScope(MigrationBudget budget, CancellationToken cancellationToken, TimeSpan timeout)
    {
        budget.Validate();
        if (timeout <= TimeSpan.Zero || timeout > budget.TransactionTimeout) throw new ArgumentOutOfRangeException(nameof(timeout));
        this.budget = budget;
        externalCancellation = cancellationToken;
        timer = new CancellationTokenSource();
        deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
        timer.CancelAfter(timeout);
    }

    public CancellationToken Token => deadline.Token;
    internal bool TransactionDeadlineExpired => timer.IsCancellationRequested && !externalCancellation.IsCancellationRequested;

    public static MigrationBudgetScope Start(MigrationBudget budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(budget);
        budget.Validate();
        return new MigrationBudgetScope(budget, cancellationToken, budget.TransactionTimeout);
    }

    public async Task ConfigureTransactionAsync(WebApiDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db.Database.CurrentTransaction is null || db.Database.GetDbConnection().State != ConnectionState.Open)
            throw new InvalidOperationException("migration_budget_requires_open_tracked_transaction");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(Token, cancellationToken);
        var version = await db.Database.SqlQueryRaw<int>(
            "SELECT pg_catalog.current_setting('server_version_num')::int AS \"Value\"").SingleAsync(linked.Token);
        if (version is < 170000 or >= 180000)
            throw new InvalidOperationException("migration_budget_unsupported_postgresql");

        var transactionMs = checked((int)budget.TransactionTimeout.TotalMilliseconds);
        var lockMs = Math.Min(transactionMs, 5000);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT pg_catalog.set_config('transaction_timeout',
              (CASE WHEN (SELECT setting::int FROM pg_catalog.pg_settings WHERE name='transaction_timeout')=0
                THEN {transactionMs} ELSE LEAST({transactionMs},(SELECT setting::int FROM pg_catalog.pg_settings WHERE name='transaction_timeout')) END)::text || 'ms',true),
              pg_catalog.set_config('statement_timeout',
              (CASE WHEN (SELECT setting::int FROM pg_catalog.pg_settings WHERE name='statement_timeout')=0
                THEN {transactionMs} ELSE LEAST({transactionMs},(SELECT setting::int FROM pg_catalog.pg_settings WHERE name='statement_timeout')) END)::text || 'ms',true),
              pg_catalog.set_config('lock_timeout',
              (CASE WHEN (SELECT setting::int FROM pg_catalog.pg_settings WHERE name='lock_timeout')=0
                THEN {lockMs} ELSE LEAST({lockMs},(SELECT setting::int FROM pg_catalog.pg_settings WHERE name='lock_timeout')) END)::text || 'ms',true)
            """, linked.Token);
    }

    public void Dispose() { deadline.Dispose(); timer.Dispose(); }
}
