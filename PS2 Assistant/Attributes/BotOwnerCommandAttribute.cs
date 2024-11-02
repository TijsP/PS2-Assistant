namespace PS2_Assistant.Attributes
{
    /// <summary>
    /// Marks the (sub)module as only being accessible to the owner of the bot (on the test server)
    /// </summary>
    [System.AttributeUsage(AttributeTargets.All, Inherited = false, AllowMultiple = true)]
    sealed class BotOwnerCommandAttribute : Attribute
    {
        public BotOwnerCommandAttribute() { }
    }
}
