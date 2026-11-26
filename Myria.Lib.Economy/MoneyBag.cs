namespace Myria.Lib.Core.Entities.Characters
{
    /// <summary>
    /// Holds the player's money. Enforces an optional capacity ceiling (upgradeable).
    /// Behaves like an inventory slot for currency — use TryAdd/TrySpend for mutations.
    /// </summary>
    public class MoneyBag
    {
        // (oldBronzeTotal, newBronzeTotal) - kept as a plain Action rather than a dedicated
        // EventArgs type since this project (Myria.Lib.Economy) doesn't reference Myria.Lib.Core
        // (where the Health/Mana *ChangedEventArgs types live).
        public event Action<long, long>? BalanceChanged;

        public Money Balance { get; set; } = new(0);
        public long Capacity { get; set; } = 300_000;
        public bool IsUnlimited => Capacity == long.MaxValue;

        // Negative amounts are refused by every mutation: "spending" a negative cost used to credit it,
        // and an overflowed price*quantity (player shop) turned into a ~9.2e18 credit that way
        // (security review 2026-11-26, A06 F2). CanHold is written so a huge amount can't overflow.
        public bool CanAfford(long cost)  => cost >= 0 && Balance.BronzeTotal >= cost;
        public bool CanHold(long amount)  => amount >= 0 && (IsUnlimited || amount <= Capacity - Balance.BronzeTotal);

        public bool TryAdd(long amount)
        {
            if (!CanHold(amount)) return false;
            long old = Balance.BronzeTotal;
            Balance += new Money(amount);
            BalanceChanged?.Invoke(old, Balance.BronzeTotal);
            return true;
        }

        public bool TrySpend(long amount)
        {
            if (!CanAfford(amount)) return false;
            long old = Balance.BronzeTotal;
            Balance -= new Money(amount);
            BalanceChanged?.Invoke(old, Balance.BronzeTotal);
            return true;
        }

        /// <summary>Sets the balance to an absolute value (server-authoritative sync) and fires
        /// <see cref="BalanceChanged"/> if it actually changed - unlike assigning
        /// <see cref="Balance"/> directly, which UI listeners never see.</summary>
        public void SetBalance(long total)
        {
            long old = Balance.BronzeTotal;
            if (total == old) return;
            Balance = new Money(total);
            BalanceChanged?.Invoke(old, total);
        }

        public override string ToString() => Balance.ToString();

        /// <summary>Compatibility alias for Balance (old API used Coins).</summary>
        public Money Coins => Balance;
    }
}
