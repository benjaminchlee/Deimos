// using System;
// using System.Linq;
// using Microsoft.CodeAnalysis.Scripting;
// using Microsoft.CodeAnalysis.CSharp.Scripting;
// using Deedle;
// using System.Threading.Tasks;
// using System.Collections.Generic;


//https://www.strathweb.com/2018/01/easy-way-to-create-a-c-lambda-expression-from-a-string-with-roslyn/
public class AsyncRoslin
{
    /*
    public async Task<Frame<int, string>> Filter(Frame<int, string> df , string filter)
    {
        //var discountFilter = "album => album.Quantity > 0";
        var options = ScriptOptions.Default.AddReferences(typeof(KeyValuePair<int, ObjectSeries<string>>).Assembly);

        Func<KeyValuePair<int, ObjectSeries<string>>, bool> filterExpression = await CSharpScript.EvaluateAsync<Func<KeyValuePair<int, ObjectSeries<string>>, bool>>(filter, options);
        df = Frame.FromRows(df.Rows.Where(filterExpression));
        return df;
    }
    */
}