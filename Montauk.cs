//
// Montauk is a tiny minimal web framework for .NET
//
// Updated On: 3 November 2011
// 
// Frank Hale <frankhale@gmail.com>
//
// GPL version 3 <http://www.gnu.org/licenses/gpl-3.0.html>
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.
//

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.SessionState;

#region ASSEMBLY INFORMATION
[assembly: AssemblyTitle("Montauk")]
[assembly: AssemblyDescription("A super tiny minimal web framework for .NET")]
[assembly: AssemblyCompany("Frank Hale")]
[assembly: AssemblyProduct("Montauk")]
[assembly: AssemblyCopyright("Copyright © 2011")]
[assembly: ComVisible(false)]
[assembly: AssemblyVersion("1.0.2")]
#endregion

namespace Montauk
{
  #region MONTAUK CONFIG
  internal class MontaukConfig
  {
    public static bool Debug = false;
    public static Type ViewEngineType = typeof(SimpleViewEngine);
    public static string DefaultRoute = "/Index";
    public static string ViewRoot = "/Views";
    public static string SharedFolderName = "Shared";
    public static string PublicResourcesFolderName = "Resources"; 
    public static string TemplatesSessionName = "__Templates";
    public static string CompiledViewsSessionName = "__CompiledViews";
    public static string AntiForgeryTokenSessionName = "__AntiForgeryTokens";
    public static string AntiForgeryTokenName = "AntiForgeryToken";
    public static string AntiForgeryTokenMissing = "All posted forms must have a valid antiforgery token.";
    public static Regex PathStaticFileRE = new Regex(@"\.(js|png|jpg|ico|css|txt)$");
  }
  #endregion

  #region MONTAUK ENGINE
  public class MontaukEngine
  {
    private HttpContext context;
    private string route;

    private List<Type> GetTypeList(Type t)
    {
      List<Type> types = null;

      types = (from assembly in AppDomain.CurrentDomain.GetAssemblies()
               from type in assembly.GetTypes()
               where type.BaseType == t
               select type).ToList();

      return types;
    }

    private Type GetApplication()
    {
      return GetTypeList(typeof(MontaukApplication)).FirstOrDefault();
    }

    public MontaukEngine(HttpContext ctx)
    {
      context = ctx;

      if (ctx.Request.Path.StartsWith("/" + MontaukConfig.PublicResourcesFolderName))
      {
        string path = context.Server.MapPath(ctx.Request.Path);

        if (MontaukConfig.PathStaticFileRE.IsMatch(path))
        {
          if (File.Exists(path))
          {
            string contentType = String.Empty;

            if (path.EndsWith(".css"))
              contentType = "text/css";
            else if (path.EndsWith(".js"))
              contentType = "application/x-javascript";
            else if (path.EndsWith(".jpg"))
              contentType = "image/jpg";
            else if (path.EndsWith(".ico"))
              contentType = "image/x-icon";
            else if (path.EndsWith(".txt"))
              contentType = "text/plain";

            context.Response.ContentType = contentType;
            context.Response.WriteFile(path);
            return;
          }
        }
      }
      else
      {
        #region ACTION HANDLER
        if ((ctx.Request.Path == "/") || (ctx.Request.Path.ToLower() == "/default.aspx"))
          ctx.Response.Redirect(MontaukConfig.DefaultRoute);

        route = ctx.Request.Path;

        Type app = GetApplication();

        if (app != null)
        {
          MontaukApplication mApp = (MontaukApplication)Activator.CreateInstance(app);
          MethodInfo initMethod = app.GetMethod("Init", BindingFlags.NonPublic | BindingFlags.Instance);
          initMethod.Invoke(mApp, new object[] { context });

          switch (ctx.Request.RequestType)
          {
            case "GET":
              string getRouteKey = mApp.Get.Keys.OrderByDescending(x => x.Length).FirstOrDefault(x => (route == x) || (route.StartsWith(x)));

              if (!String.IsNullOrEmpty(getRouteKey))
              {
                mApp.CurrentActionRoute = getRouteKey.ReplaceFirstInstance("/", "");
                string[] routeArgs = route.Replace(getRouteKey, String.Empty).Split(new string[] { "/" }, StringSplitOptions.RemoveEmptyEntries);
                string view = mApp.Get[getRouteKey](routeArgs);
                context.Response.Write(view);
                return;
              }
              break;

            case "DELETE":
            case "PUT":
            case "POST":
              string postRouteKey = mApp.Post.Keys.OrderByDescending(x => x.Length).FirstOrDefault(x => route == x);

              if (!String.IsNullOrEmpty(postRouteKey))
              {
                if (AntiForgeryToken.VerifyToken(context))
                {
                  AntiForgeryToken.RemoveToken(context);

                  mApp.CurrentActionRoute = postRouteKey.ReplaceFirstInstance("/", "");
                  string view = mApp.Post[postRouteKey](context.Request.Form);
                  context.Response.Write(view);
                  return;
                }
                else
                  throw new Exception(MontaukConfig.AntiForgeryTokenMissing);
              }
              break;
          };
        }
        #endregion
      }

      context.Response.ContentType = "text/html";
      context.Response.Write("Http 404 Not Found");
    }
  }
  #endregion

  #region MONTAUK APPLICATION
  public abstract class MontaukApplication
  {
    public HttpContext Context { get; internal set; }
    public HttpRequest Request { get; internal set; }
    public HttpResponse Response { get; internal set; }

    public string CurrentActionRoute { get; internal set; }

    public Dictionary<string, string> ViewTags { get; internal set; }

    public Dictionary<string, Func<string[], string>> Get = new Dictionary<string, Func<string[], string>>();
    public Dictionary<string, Func<NameValueCollection, string>> Post = new Dictionary<string, Func<NameValueCollection, string>>();

    private IViewEngine viewEngine;

    public MontaukApplication()
    {
      ViewTags = new Dictionary<string, string>();
    }

    protected void Init(HttpContext ctx)
    {
      Context = ctx;
      Request = ctx.Request;
      Response = ctx.Response;

      viewEngine = (IViewEngine)Activator.CreateInstance(MontaukConfig.ViewEngineType, new object[] { Context });
    }

    public void WriteHTML(string html)
    {
      if (Response.ContentType != "text/html")
        Response.ContentType = "text/html";

      Response.Write(html);
    }

    public string View()
    {
      viewEngine.LoadView(CurrentActionRoute, ViewTags);

      return viewEngine[CurrentActionRoute];
    }

    public string View(string route)
    {
      viewEngine.LoadView(route, ViewTags);

      return viewEngine[route];
    }
  }
  #endregion

  #region ANTIFORGERYTOKEN
  internal class AntiForgeryToken
  {
    private static List<string> GetTokens(HttpContext context)
    {
      List<string> tokens = null;

      if (context.Session[MontaukConfig.AntiForgeryTokenSessionName] != null)
        tokens = context.Session[MontaukConfig.AntiForgeryTokenSessionName] as List<string>;
      else
        tokens = new List<string>();

      return tokens;
    }

    private static string CreateUniqueToken(List<string> tokens)
    {
      string token = (Guid.NewGuid().ToString() + Guid.NewGuid().ToString()).Replace("-", "");

      if (tokens.Contains(token))
        CreateUniqueToken(tokens);

      return token;
    }

    public static string Create(HttpContext context)
    {
      List<string> tokens = GetTokens(context);
      string token = CreateUniqueToken(tokens);
      tokens.Add(token);

      context.Session[MontaukConfig.AntiForgeryTokenSessionName] = tokens;

      return String.Format("<input type=\"hidden\" name=\"AntiForgeryToken\" value=\"{0}\" />", token);
    }

    public static void RemoveToken(HttpContext context)
    {
      if (context.Request.Form.AllKeys.Contains(MontaukConfig.AntiForgeryTokenName))
      {
        string token = context.Request.Form[MontaukConfig.AntiForgeryTokenName];

        List<string> tokens = GetTokens(context);

        if (tokens.Contains(token))
        {
          tokens.Remove(token);

          context.Session[MontaukConfig.AntiForgeryTokenSessionName] = tokens;
        }
      }
    }

    public static bool VerifyToken(HttpContext context)
    {
      if (context.Request.Form.AllKeys.Contains(MontaukConfig.AntiForgeryTokenName))
      {
        string token = context.Request.Form[MontaukConfig.AntiForgeryTokenName];

        return GetTokens(context).Contains(token);
      }

      return false;
    }
  }
  #endregion

  #region SIMPLE VIEW ENGINE
  public interface IViewEngine
  {
    void LoadView(string viewKeyName, Dictionary<string, string> tags);
    bool ContainsView(string view);
    string this[string view] { get; }
  }

  internal class SimpleViewEngine : IViewEngine
  {
    private HttpContext context;
    private string viewRoot;
    private Dictionary<string, string> views;
    private static Regex directiveTokenRE = new Regex(@"(\%\%(?<directive>[a-zA-Z0-9]+)=(?<value>[a-zA-Z0-9]+)\%\%)");
    private Regex headBlockRE = new Regex(@"\[\[(?<block>[\s\w\p{P}\p{S}]+)\]\]");
    private string tagFormatPattern = @"({{({{|\|){0}(\||}})}})";
    private string tagPattern = @"{({|\|)([\w]+)(}|\|)}";
    private string tagEncodingHint = "|";
    private static string antiForgeryToken = String.Format("%%{0}%%", MontaukConfig.AntiForgeryTokenName);
    private static string viewDirective = "%%View%%";
    private static string headDirective = "%%Head%%";
    private static string partialDirective = "%%Partial={0}%%";
    private static string sharedFolderName = MontaukConfig.SharedFolderName;
    private Dictionary<string, StringBuilder> templates;

    public SimpleViewEngine(HttpContext ctx)
    {
      context = ctx;
      viewRoot = MontaukConfig.ViewRoot;

      // Load all templates here and then cache them in the application store
      if ((context.Application[MontaukConfig.TemplatesSessionName] != null) && (!MontaukConfig.Debug))
      {
        templates = context.Application[MontaukConfig.TemplatesSessionName] as Dictionary<string, StringBuilder>;
        views = context.Application[MontaukConfig.CompiledViewsSessionName] as Dictionary<string, string>;
      }
      else
      {
        templates = new Dictionary<string, StringBuilder>();

        LoadTemplates(viewRoot);

        context.Application[MontaukConfig.TemplatesSessionName] = templates;
        context.Application[MontaukConfig.CompiledViewsSessionName] = views = new Dictionary<string, string>();
      }
    }

    private void LoadTemplates(string path)
    {
      string root = context.Server.MapPath(MontaukConfig.ViewRoot);

      foreach (FileInfo fi in GetFiles(context.Server.MapPath(path)))
      {
        string template;
        
        using (StreamReader sr = new StreamReader(fi.OpenRead()))
        {
          template = sr.ReadToEnd();
        }
        
        string viewKeyName = fi.FullName.Replace(root, "").Replace(".html", "").ReplaceFirstInstance(@"\", "");

        templates.Add(viewKeyName, new StringBuilder(template));
      }
    }

    // This code was adapted to work with FileInfo but was originally from the following question on SO:
    //
    // http://stackoverflow.com/questions/929276/how-to-recursively-list-all-the-files-in-a-directory-in-c
    private static IEnumerable<FileInfo> GetFiles(string path)
    {
      Queue<string> queue = new Queue<string>();
      queue.Enqueue(path);
      while (queue.Count > 0)
      {
        path = queue.Dequeue();
        try
        {
          foreach (string subDir in Directory.GetDirectories(path))
          {
            queue.Enqueue(subDir);
          }
        }
        catch (Exception ex)
        {
          Console.Error.WriteLine(ex);
        }

        FileInfo[] fileInfos = null;
        try
        {
          fileInfos = new DirectoryInfo(path).GetFiles();
        }
        catch
        {
          throw;
        }
        if (fileInfos != null)
        {
          for (int i = 0; i < fileInfos.Length; i++)
          {
            yield return fileInfos[i];
          }
        }
      }
    }

    private StringBuilder ProcessDirectives(string viewKeyName, StringBuilder rawView)
    {
      MatchCollection dirMatches = directiveTokenRE.Matches(rawView.ToString());
      StringBuilder pageContent = new StringBuilder();
      StringBuilder directive = new StringBuilder();
      StringBuilder value = new StringBuilder();

      #region PROCESS KEY=VALUE DIRECTIVES (MASTER AND PARTIAL VIEWS)
      foreach (Match match in dirMatches)
      {
        directive.Length = 0;
        directive.Insert(0, match.Groups["directive"].Value);

        value.Length = 0;
        value.Insert(0, match.Groups["value"].Value);

        string template = templates[MontaukConfig.SharedFolderName + Path.DirectorySeparatorChar + value].ToString();

        //FIXME: The switch statement below needs more work!

        switch (directive.ToString())
        {
          case "Master":
            pageContent = new StringBuilder(template);
            rawView.Replace(match.Groups[0].Value, String.Empty);
            pageContent.Replace(viewDirective, rawView.ToString());
            break;

          case "Partial":
            StringBuilder partialContent = new StringBuilder(template);
            rawView.Replace(String.Format(partialDirective, value), partialContent.ToString());
            break;
        }
      }
      #endregion

      // If during the process of building the view we have more directives to process
      // we'll recursively call ProcessDirectives to take care of them
      if (directiveTokenRE.Matches(pageContent.ToString()).Count > 0)
        ProcessDirectives(viewKeyName, pageContent);

      #region PROCESS HEAD SUBSTITUTIONS AFTER ALL TEMPLATES HAVE BEEN COMPILED
      MatchCollection heads = headBlockRE.Matches(pageContent.ToString());

      if (heads.Count > 0)
      {
        StringBuilder headSubstitutions = new StringBuilder();

        foreach (Match head in heads)
        {
          headSubstitutions.Append(Regex.Replace(head.Groups["block"].Value, @"^(\s+)", string.Empty, RegexOptions.Multiline));
          pageContent.Replace(head.Value, String.Empty);
        }

        pageContent.Replace(headDirective, headSubstitutions.ToString());
      }

      pageContent.Replace(headDirective, String.Empty);
      #endregion

      return pageContent;
    }

    private void Compile(string viewKeyName, Dictionary<string, string> tags)
    {
      StringBuilder rawView = new StringBuilder(templates[viewKeyName].ToString());
      StringBuilder compiledView = new StringBuilder();

      compiledView = ProcessDirectives(viewKeyName, rawView);

      if (String.IsNullOrEmpty(compiledView.ToString()))
        compiledView = rawView;

      compiledView.Replace(antiForgeryToken, AntiForgeryToken.Create(context));

      StringBuilder tagSB = new StringBuilder();

      foreach (KeyValuePair<string, string> tag in tags)
      {
        tagSB.Length = 0;
        tagSB.Insert(0, String.Format(tagFormatPattern, tag.Key));

        Regex nonHTMLEncodedTagRE = new Regex(tagSB.ToString());

        if (nonHTMLEncodedTagRE.IsMatch(compiledView.ToString()))
        {
          MatchCollection nonEncodedMatches = nonHTMLEncodedTagRE.Matches(compiledView.ToString());

          foreach (Match m in nonEncodedMatches)
          {
            if (!m.Value.Contains(tagEncodingHint))
              compiledView.Replace(m.Value, tag.Value);
            else
              compiledView.Replace(m.Value, HttpUtility.HtmlEncode(tag.Value));
          }
        }
      }

      Regex leftOverTags = new Regex(tagPattern);

      if (leftOverTags.IsMatch(compiledView.ToString()))
      {
        MatchCollection m = leftOverTags.Matches(compiledView.ToString());

        foreach (Match match in m)
        {
          compiledView.Replace(match.Value, String.Empty);
        }
      }

      views[viewKeyName] = Regex.Replace(compiledView.ToString(), @"^\s*$\n", string.Empty, RegexOptions.Multiline);
    }

    public void LoadView(string viewKeyName, Dictionary<string, string> tags)
    {
      viewKeyName = FixateViewKeyName(viewKeyName);
      viewKeyName = FixateIndexKey(viewKeyName);

      if (templates.ContainsKey(viewKeyName))
        Compile(viewKeyName, tags);
    }

    private string FixateIndexKey(string key)
    {
      if (key == "Index")
        return String.Format(@"Index{0}Index", Path.DirectorySeparatorChar);

      return key;
    }

    private string FixateViewKeyName(string key)
    {
      return key.Replace("/", Path.DirectorySeparatorChar.ToString());
    }

    public bool ContainsView(string view)
    {
      return views.ContainsKey(view);
    }

    public string this[string key]
    {
      get
      {
        key = FixateIndexKey(key);
        key = FixateViewKeyName(key);

        if (!views.ContainsKey(key))
          throw new Exception(String.Format("Cannot find view named: {0}", key));

        return views[key];
      }
    }
  }
  #endregion

  #region EXTENSION METHODS
  public static class Extensions
  {
    // Grabbed this extension method from an answer to this question:
    //
    // http://stackoverflow.com/questions/141045/how-do-i-replace-the-first-instance-of-a-string-in-net
    public static string ReplaceFirstInstance(this string source, string find, string replace)
    {
      int index = source.IndexOf(find);
      return index < 0 ? source : source.Substring(0, index) + replace +
           source.Substring(index + find.Length);
    }
  }
  #endregion

  #region HTTP HANDLER
  public class MontaukHandler : IHttpHandler, IRequiresSessionState
  {
    public bool IsReusable
    {
      get { return false; }
    }

    public void ProcessRequest(HttpContext ctx)
    {
      new MontaukEngine(ctx);
    }
  }
  #endregion
}