namespace TriviaServer.Models
{
    public class Question
    {
        public int Id { get; set; }
        public string Text { get; set; } = "";
        public string Ans1 { get; set; } = "";
        public string Ans2 { get; set; } = "";
        public string Ans3 { get; set; } = "";
        public string Ans4 { get; set; } = "";
    }
}
