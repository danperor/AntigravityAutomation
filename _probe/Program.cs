using Microsoft.Playwright;

var pw = await Playwright.CreateAsync();
var browser = await pw.Chromium.ConnectOverCDPAsync("http://127.0.0.1:12469");
var context = browser.Contexts[0];
var page = context.Pages[0];
Console.WriteLine($"页面标题: {await page.TitleAsync()}");
Console.WriteLine($"URL: {page.Url}");

// 搜索所有文本节点（含shadow DOM）
var results = await page.EvaluateAsync<string>("(function(){" +
  "var found=[];" +
  "function search(root,label){" +
    "var w=document.createTreeWalker(root,NodeFilter.SHOW_TEXT,null);" +
    "while(w.nextNode()){var t=w.currentNode.textContent;" +
      "if(t&&t.match(/allow this time/i)){var el=w.currentNode.parentElement;" +
        "found.push({where:label,tag:el?el.tagName:'',cls:el?(el.className||''):'',text:t.trim().substring(0,80)});}}" +
  "}" +
  "search(document.body,'main');" +
  "document.querySelectorAll('*').forEach(function(el){if(el.shadowRoot)search(el.shadowRoot,'shadow:'+el.tagName);});" +
  "return JSON.stringify(found);" +
"})()");

Console.WriteLine($"搜索结果: {results}");

// 搜索quick-input / notification / monaco-list 容器
var containerText = await page.EvaluateAsync<string?>("(function(){" +
  "var qs=document.querySelectorAll('.quick-input-widget,.quick-input-list,.notification-toast,.notifications-toasts,.monaco-list,.quick-input');" +
  "var parts=[];qs.forEach(function(q){parts.push(q.className+': '+q.textContent.substring(0,200));});" +
  "return parts.length?parts.join('\\n---\\n'):null;" +
"})()");
Console.WriteLine($"QuickInput/Notification容器:");
Console.WriteLine(containerText ?? "(无)");

// 搜索所有iframe
var iframeCount = await page.EvaluateAsync<int>("document.querySelectorAll('iframe').length");
Console.WriteLine($"iframe数量: {iframeCount}");

await browser.CloseAsync();
pw.Dispose();
