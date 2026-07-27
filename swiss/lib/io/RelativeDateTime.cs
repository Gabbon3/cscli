namespace lib.io
{
    public enum RelativeDateTimeField
    {
        Modified,
        Created,
        Accessed
    }

    public readonly struct RelativeDateTime
    {
        public RelativeDateTimeField Field { get; }
        public DateTime ValueUtc { get; }

        public RelativeDateTime(RelativeDateTimeField field, DateTime valueUtc)
        {
            Field = field;
            ValueUtc = valueUtc;
        }
    }
}
