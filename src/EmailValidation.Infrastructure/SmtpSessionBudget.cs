using EmailValidation.Core;

namespace EmailValidation.Infrastructure;

public sealed class SmtpSessionBudget : ISmtpSessionBudget
{
    private readonly AsyncLocal<BudgetState?> _current = new();

    public IDisposable Begin(int maximumSessions)
    {
        var previous = _current.Value;
        _current.Value = new BudgetState(Math.Max(1, maximumSessions));
        return new Scope(_current, previous);
    }

    public bool TryConsume()
    {
        var state = _current.Value;
        if (state is null) return true;
        lock (state)
        {
            if (state.Remaining == 0) return false;
            state.Remaining--;
            return true;
        }
    }

    private sealed class BudgetState(int remaining)
    {
        public int Remaining { get; set; } = remaining;
    }

    private sealed class Scope(AsyncLocal<BudgetState?> current, BudgetState? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            current.Value = previous;
            _disposed = true;
        }
    }
}
