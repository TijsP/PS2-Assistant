namespace PS2_Assistant.Models.Census.API
{
    /// <summary>
    /// generic
    /// </summary>
    interface ICensusObject
    {
        /// <summary>
        /// Contains the collection and the query required to get the desired data. Any specifiers can be added to the end of this string, i.e. <example>"&name.first_lower=name"</example>
        /// </summary>
        public static abstract string CollectionQuery { get; }
    }
}
