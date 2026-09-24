namespace MonsterAdventure.Core
{
    /// <summary>한국어 조사 처리(받침 유무). 웹의 josa/J와 같다.</summary>
    public static class Korean
    {
        public static string Josa(string word, string withBatchim, string withoutBatchim)
        {
            int code = word[word.Length - 1] - 0xAC00;
            return code >= 0 && code < 11172 && code % 28 != 0 ? withBatchim : withoutBatchim;
        }

        /// <summary>단어 + 조사. 예: J("불꼬마", "은", "는") → "불꼬마는"</summary>
        public static string J(string word, string withBatchim, string withoutBatchim) =>
            word + Josa(word, withBatchim, withoutBatchim);
    }
}
