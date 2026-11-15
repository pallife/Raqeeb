package com.raqeeb.app;

import android.app.Activity;
import android.content.Intent;
import android.graphics.Color;
import android.net.Uri;
import android.os.Bundle;
import android.view.View;
import android.view.WindowManager;
import android.webkit.WebChromeClient;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;

public class MainActivity extends Activity {
    private static final String HOST = "rq.80-241-218-54.sslip.io";
    private WebView web;

    @Override protected void onCreate(Bundle b) {
        super.onCreate(b);
        web = new WebView(this);
        web.setBackgroundColor(Color.parseColor("#070b12"));
        setContentView(web);
        WebSettings s = web.getSettings();
        s.setJavaScriptEnabled(true); s.setDomStorageEnabled(true);
        s.setMediaPlaybackRequiresUserGesture(false);
        web.setWebViewClient(new WebViewClient() {
            @Override public boolean shouldOverrideUrlLoading(WebView v, android.webkit.WebResourceRequest r) {
                if (HOST.equals(r.getUrl().getHost())) return false;
                startActivity(new Intent(Intent.ACTION_VIEW, r.getUrl())); return true;
            }
        });
        web.setWebChromeClient(new WebChromeClient());
        load(getIntent());
    }

    private void load(Intent i) {
        Uri u = i != null ? i.getData() : null;
        // رابط الاقتران يحوي السرّ بعد # ويبقى داخل التطبيق فقط
        web.loadUrl(u != null && HOST.equals(u.getHost()) ? u.toString() : "https://" + HOST + "/");
    }

    @Override protected void onNewIntent(Intent i) { super.onNewIntent(i); setIntent(i); load(i); }
    @Override public void onBackPressed() { if (web.canGoBack()) web.goBack(); else super.onBackPressed(); }
    @Override protected void onResume() { super.onResume(); web.onResume(); }
    @Override protected void onPause() { web.onPause(); super.onPause(); }
}
