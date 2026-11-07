document.addEventListener("DOMContentLoaded", () => {
  const statusBadge = document.getElementById("statusBadge");
  const statusText = document.getElementById("statusText");
  const txtUrl = document.getElementById("txtUrl");
  const btnDownload = document.getElementById("btnDownload");
  const mediaList = document.getElementById("mediaList");
  const mediaCount = document.getElementById("mediaCount");
  const toggleIntercept = document.getElementById("toggleIntercept");
  const toggleVideoBar = document.getElementById("toggleVideoBar");
  const linkHelp = document.getElementById("linkHelp");

  // 1. Check desktop app status
  checkStatus();

  function checkStatus() {
    chrome.runtime.sendMessage({ action: "checkStatus" }, (res) => {
      if (res && res.online) {
        statusBadge.className = "status-badge online";
        statusText.textContent = `IDMLite Bağlı (${res.activeCount || 0} aktif)`;
      } else {
        statusBadge.className = "status-badge offline";
        statusText.textContent = "IDMLite Kapalı";
      }
    });
  }

  // 2. Load preferences
  chrome.storage.local.get(["autoIntercept", "showVideoBar"], (res) => {
    toggleIntercept.checked = res.autoIntercept ?? true;
    toggleVideoBar.checked = res.showVideoBar ?? true;
  });

  toggleIntercept.addEventListener("change", () => {
    chrome.storage.local.set({ autoIntercept: toggleIntercept.checked });
  });

  toggleVideoBar.addEventListener("change", () => {
    chrome.storage.local.set({ showVideoBar: toggleVideoBar.checked });
  });

  // 3. Quick URL download
  btnDownload.addEventListener("click", () => {
    const url = txtUrl.value.trim();
    if (!url || !url.startsWith("http")) {
      alert("Lütfen geçerli bir indirme URL'si girin.");
      return;
    }

    chrome.runtime.sendMessage({
      action: "downloadUrl",
      payload: {
        url: url,
        autoStart: false
      }
    }, (res) => {
      if (res && res.success) {
        txtUrl.value = "";
        btnDownload.textContent = "Gönderildi ✓";
        setTimeout(() => { btnDownload.textContent = "İndir"; }, 1500);
      } else {
        alert("IDMLite masaüstü uygulamasına bağlanılamadı. Uygulamanın açık olduğundan emin olun.");
      }
    });
  });

  // 4. Load media streams for active tab
  chrome.tabs.query({ active: true, currentWindow: true }, (tabs) => {
    if (!tabs || tabs.length === 0) return;
    const activeTab = tabs[0];

    chrome.runtime.sendMessage({ action: "getTabMedia", tabId: activeTab.id }, (res) => {
      const streams = res?.media || [];
      mediaCount.textContent = streams.length;

      if (streams.length > 0) {
        mediaList.innerHTML = "";
        streams.forEach((item) => {
          const card = document.createElement("div");
          card.className = "media-card";

          const fileName = item.url.split("/").pop()?.split("?")[0] || "Medya Dosyası";
          let sizeStr = "";
          if (item.size > 0) {
            sizeStr = formatBytes(item.size);
          } else if (item.url.includes(".m3u8")) {
            sizeStr = "HLS Stream";
          } else {
            sizeStr = item.contentType || "Stream";
          }

          const info = document.createElement("div");
          info.className = "media-info";
          const title = document.createElement("span");
          title.className = "media-title";
          title.title = item.url;
          title.textContent = fileName;
          const subtitle = document.createElement("span");
          subtitle.className = "media-sub";
          subtitle.textContent = sizeStr;
          const button = document.createElement("button");
          button.className = "media-download-btn";
          button.textContent = "İndir";
          info.append(title, subtitle);
          card.append(info, button);

          card.querySelector("button").addEventListener("click", () => {
            chrome.runtime.sendMessage({
              action: "downloadUrl",
              payload: {
                url: item.url,
                fileName: fileName,
                referer: activeTab.url,
                category: "Video",
                autoStart: false
              }
            }, (downloadRes) => {
              if (downloadRes && downloadRes.success) {
                alert("İndirme IDMLite'a aktarıldı!");
              } else {
                alert("Masaüstü uygulamasına ulaşılamadı.");
              }
            });
          });

          mediaList.appendChild(card);
        });
      }
    });
  });

  linkHelp.addEventListener("click", (e) => {
    e.preventDefault();
    alert("IDMLite Eklentisi Kılavuzu:\n\n• Sayfalardaki video oynatıcıların üzerinde beliren 'Bu Videoyu İndir' butonunu kullanabilirsiniz.\n• İndirmeler otomatik olarak yakalanır ve IDMLite masaüstü uygulamasına aktarılır.");
  });

  function formatBytes(bytes) {
    if (!bytes || bytes <= 0) return "0 B";
    const units = ["B", "KB", "MB", "GB"];
    let i = 0;
    while (bytes >= 1024 && i < units.length - 1) {
      bytes /= 1024;
      i++;
    }
    return bytes.toFixed(1) + " " + units[i];
  }
});
