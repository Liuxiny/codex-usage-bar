({
  request: {
    url: "{{baseUrl}}/api/user/self/package-quota",
    method: "GET",
    headers: { "Authorization": "Bearer {{accessToken}}", "User-Agent": "cc-switch/1.0" }
  },
  extractor: function (response) {
    var d = response && response.data;
    if (!response || !response.success || !d)
      return { isValid: false, invalidMessage: (response && response.message) || "查询失败" };
    function numeric(n) { return typeof n === "number" && isFinite(n); }
    var now = Date.now();
    var age = now - Date.parse(d.updated_at);
    var fresh = isFinite(age) && age >= -60000 && age <= 900000 &&
      (d.status === "available" || d.status === "exhausted");
    var windows = Array.isArray(d.windows) ? d.windows : [];
    function find(seconds) { return windows.find(function (w) { return w.window_seconds === seconds; }); }
    function usable(w) {
      var reset = w && Date.parse(w.reset_at);
      return fresh && w && numeric(w.remaining_percent) && w.remaining_percent >= 0 &&
        w.remaining_percent <= 100 && isFinite(reset) && reset > now;
    }
    var wallet = d.site_balance;
    var balance = wallet && wallet.status === "available" && numeric(wallet.remaining) ? wallet.remaining : null;
    var week = find(604800);
    var estimateWindows = d.estimation && d.estimation.windows;
    var rate = Array.isArray(estimateWindows) && week ? estimateWindows.find(function (w) {
      return w.name === week.name && w.status === "ready";
    }) : null;
    var plan = (d.plan || "套餐").toUpperCase();
    function item(label, value, unit, note) {
      var row = { planName: plan + " · " + label, extra: note, isValid: numeric(value) };
      if (numeric(value)) { row.remaining = value; row.unit = unit; }
      else { row.invalidMessage = "暂不可用"; }
      return row;
    }
    var rows = [item("站内余额", balance, "USD", "个人余额")];
    function quota(label, window) {
      var row = item(label, usable(window) ? window.remaining_percent : null, "%", "同一套餐 · 共享剩余额度");
      var reset = window && Date.parse(window.reset_at);
      if (isFinite(reset)) {
        var date = new Date(reset + 8 * 3600000);
        function pad(n) { return (n < 10 ? "0" : "") + n; }
        var stamp = pad(date.getUTCMonth() + 1) + "/" + pad(date.getUTCDate()) +
          " " + pad(date.getUTCHours()) + ":" + pad(date.getUTCMinutes());
        row.extra = "重置 " + stamp + " (UTC+8)";
        if (fresh && reset > now) {
          var minutes = Math.ceil((reset - now) / 60000);
          var days = Math.floor(minutes / 1440);
          row.extra += " · " + (days ? days + "天" : "") + Math.floor(minutes % 1440 / 60) + "小时" + minutes % 60 + "分后";
        } else { row.extra += " · 等待更新"; }
        if (reset <= now) row.invalidMessage = "已到重置时间";
      } else { row.extra = "重置时间暂不可用"; }
      if (row.isValid) row.total = 100;
      rows.push(row);
    }
    if (String(d.plan || "").toLowerCase() !== "pro") quota("5小时", find(18000));
    quota("周", week);
    var equivalent = null;
    if (balance !== null && usable(week) && rate && numeric(rate.usd_per_percentage_point) && rate.usd_per_percentage_point > 0)
      equivalent = Math.max(0, balance) / rate.usd_per_percentage_point;
    rows.push(item("余额折合周额度", equivalent, "% 周", "历史估算 · 非额外额度"));
    return rows;
  }
})
