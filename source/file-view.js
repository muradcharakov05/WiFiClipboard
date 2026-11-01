'use strict';
(() => {
  const primary = document.getElementById('safariDownload');
  const ordinary = document.getElementById('regularDownload');
  if (primary && ordinary) {
    primary.href = ordinary.href;
    primary.target = '_blank';
    primary.rel = 'noopener noreferrer';
    primary.textContent = 'Открыть скачивание';
  }
  const standalone = navigator.standalone === true || (window.matchMedia && window.matchMedia('(display-mode: standalone)').matches);
  if (standalone) {
    const notice = document.createElement('p');
    notice.setAttribute('role', 'status');
    notice.textContent = 'Сейчас сайт открыт как веб-приложение без панели Safari. Для загрузок откройте его адрес в Safari и создайте значок с выключенным «Открывать как веб-приложение».';
    const main = document.querySelector('main');
    if (main) main.prepend(notice);
  }
})();
