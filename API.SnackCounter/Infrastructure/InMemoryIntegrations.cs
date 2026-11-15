using SnackCounter.Application;
using SnackCounter.Domain;

namespace SnackCounter.Infrastructure;

public sealed class TableCurrencyConverter : ICurrencyConverter
{
    private readonly IReadOnlyDictionary<(Currency From, Currency To), decimal> _rates;

    public TableCurrencyConverter(IReadOnlyDictionary<(Currency From, Currency To), decimal> rates)
    {
        ArgumentNullException.ThrowIfNull(rates);
        if (rates.Any(pair => pair.Value <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(rates), "Exchange rates must be positive.");
        }
        _rates = new Dictionary<(Currency From, Currency To), decimal>(rates);
    }

    public decimal Convert(decimal amount, Currency from, Currency to, DateOnly date)
    {
        if (from == to)
        {
            return amount;
        }

        // The assignment does not supply an FX source, so callers must configure the simulated rate table.
        if (!_rates.TryGetValue((from, to), out var rate))
        {
            throw new InvalidOperationException($"No exchange rate is configured for {from} to {to} on {date}.");
        }

        // Two decimal places and midpoint-to-even are the demo's explicit rounding policy.
        return decimal.Round(amount * rate, 2, MidpointRounding.ToEven);
    }
}

public sealed class RoundRobinVolunteerSelector : IVolunteerSelector
{
    private readonly object _gate = new();
    private readonly IReadOnlyDictionary<OfficeLocation, IReadOnlyList<Volunteer>> _eligibleByOffice;
    private readonly Dictionary<OfficeLocation, int> _lastSelectedIndex = new();

    public RoundRobinVolunteerSelector(
        IReadOnlyDictionary<OfficeLocation, IReadOnlyList<Volunteer>> eligibleByOffice)
    {
        ArgumentNullException.ThrowIfNull(eligibleByOffice);
        // Visiting employees are eligible only when the caller includes them in that office's roster.
        _eligibleByOffice = eligibleByOffice.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<Volunteer>)entry.Value
                .GroupBy(volunteer => volunteer.EmployeeId, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray());
    }

    public Volunteer? GetNextEligibleVolunteer(OfficeLocation office, string previousEmployeeId)
    {
        lock (_gate)
        {
            if (!_eligibleByOffice.TryGetValue(office, out var eligible) || eligible.Count == 0)
            {
                return null;
            }

            var previousIndex = -1;
            for (var index = 0; index < eligible.Count; index++)
            {
                if (eligible[index].EmployeeId == previousEmployeeId)
                {
                    previousIndex = index;
                    break;
                }
            }

            var startIndex = previousIndex >= 0
                ? previousIndex
                : _lastSelectedIndex.TryGetValue(office, out var lastIndex) ? lastIndex : -1;
            var nextIndex = (startIndex + 1) % eligible.Count;
            _lastSelectedIndex[office] = nextIndex;
            return eligible[nextIndex];
        }
    }
}

public sealed class InMemoryHrSystem : IHrSystem
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, (string ClaimId, HrReimbursementClaim Claim)> _claims = new();

    public IReadOnlyList<HrReimbursementClaim> Claims
    {
        get
        {
            lock (_gate)
            {
                return _claims.Values.Select(entry => entry.Claim with { ReceiptImage = entry.Claim.ReceiptImage.ToArray() }).ToArray();
            }
        }
    }

    public string SubmitClaim(HrReimbursementClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        lock (_gate)
        {
            if (_claims.TryGetValue(claim.RefillId, out var existing))
            {
                return existing.ClaimId;
            }

            var claimId = Guid.NewGuid().ToString("N");
            var copy = claim with { ReceiptImage = claim.ReceiptImage.ToArray() };
            _claims.Add(claim.RefillId, (claimId, copy));
            return claimId;
        }
    }
}

public sealed class InMemoryChatTool : IChatTool
{
    private readonly object _gate = new();
    private readonly List<string> _notifications = new();
    private readonly HashSet<Guid> _submittedClaimNotifications = new();

    public IReadOnlyList<string> Notifications
    {
        get
        {
            lock (_gate)
            {
                return _notifications.ToArray();
            }
        }
    }

    public void NotifyRefillCreated(Refill refill) => Record($"Refill {refill.Id} opened for {refill.Item} at {refill.Office}.");

    public void NotifyVolunteerAssigned(Refill refill, Volunteer volunteer) =>
        Record($"Refill {refill.Id} assigned to {volunteer.EmployeeId}.");

    public void NotifyVolunteerHandoff(Refill refill, Volunteer previous, Volunteer next) =>
        Record($"Refill {refill.Id} handed off from {previous.EmployeeId} to {next.EmployeeId}.");

    public void NotifyClaimSubmitted(Refill refill)
    {
        lock (_gate)
        {
            if (_submittedClaimNotifications.Add(refill.Id))
            {
                _notifications.Add($"Claim submitted for refill {refill.Id}.");
            }
        }
    }

    private void Record(string message)
    {
        lock (_gate)
        {
            _notifications.Add(message);
        }
    }
}
