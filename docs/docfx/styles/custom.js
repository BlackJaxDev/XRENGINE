(function () {
  function buildNavbar() {
    var navbar = document.querySelector('#navbar');
    if (!navbar || navbar.querySelector('.xr-nav')) {
      return;
    }

    var relMeta = document.querySelector("meta[property='docfx:rel']");
    var rel = relMeta ? relMeta.getAttribute('content') : '';
    var currentPath = window.location.pathname.toLowerCase();

    var items = [
      { title: 'Overview', href: rel + 'index.html', match: /\/index\.html$/ },
      { title: 'Guides', href: rel + 'guides.html', match: /\/guides\.html$/ },
      { title: 'API Reference', href: rel + 'api/index.html', match: /\/api\// },
      { title: 'GitHub', href: 'https://github.com/BlackJaxDev/XRENGINE', external: true }
    ];

    var list = document.createElement('ul');
    list.className = 'nav navbar-nav xr-nav';

    items.forEach(function (item) {
      var li = document.createElement('li');
      var a = document.createElement('a');
      a.textContent = item.title;
      a.href = item.href;
      if (item.external) {
        a.target = '_blank';
        a.rel = 'noopener';
      }
      if (item.match && item.match.test(currentPath)) {
        li.classList.add('active');
      }
      li.appendChild(a);
      list.appendChild(li);
    });

    var searchForm = navbar.querySelector('form.navbar-form');
    if (searchForm) {
      navbar.insertBefore(list, searchForm);
    } else {
      navbar.appendChild(list);
    }
  }

  function ensureConceptualSidebar() {
    var main = document.querySelector("div[role='main'].body-content.hide-when-search");
    if (!main) {
      return;
    }

    var article = main.querySelector('.article');
    if (!article || main.querySelector('.sidenav')) {
      return;
    }

    var sidenav = document.createElement('div');
    sidenav.className = 'sidenav hide-when-search';
    sidenav.innerHTML =
      '<a class="btn toc-toggle collapse" data-toggle="collapse" href="#sidetoggle" aria-expanded="true" aria-controls="sidetoggle">Show / Hide Table of Contents</a>' +
      '<div class="sidetoggle collapse in" id="sidetoggle">' +
      '  <div id="sidetoc"></div>' +
      '</div>';

    main.insertBefore(sidenav, article);
    article.classList.add('grid-right');

    loadTocIntoSidebar(sidenav);
  }

  function loadTocIntoSidebar(sidenav) {
    var sidetoc = sidenav.querySelector('#sidetoc');
    if (!sidetoc) {
      return;
    }

    var currentPath = window.location.pathname.toLowerCase();
    var meta = document.querySelector("meta[property='docfx:tocrel']");
    var tocRel = meta ? meta.getAttribute('content') : null;

    if (currentPath.endsWith('/api/index.html')) {
      tocRel = 'toc.html';
    }

    if (!tocRel) {
      return;
    }

    var tocUrl = new URL(tocRel, window.location.href).toString();
    fetch(tocUrl)
      .then(function (response) {
        if (!response.ok) {
          throw new Error('Failed to load toc');
        }
        return response.text();
      })
      .then(function (html) {
        var parser = new DOMParser();
        var doc = parser.parseFromString(html, 'text/html');
        var source = doc.querySelector('#sidetoggle > div');
        if (!source) {
          return;
        }

        sidetoc.innerHTML = '';
        sidetoc.appendChild(document.importNode(source, true));

        var baseUrl = tocUrl.substring(0, tocUrl.lastIndexOf('/') + 1);
        var currentAbs = new URL(window.location.href);

        sidetoc.querySelectorAll('a[href]').forEach(function (link) {
          var href = link.getAttribute('href');
          if (!href) {
            return;
          }

          if (!/^(https?:)?\/\//i.test(href) && !href.startsWith('#')) {
            link.setAttribute('href', new URL(href, baseUrl).toString());
          }

          try {
            var linkUrl = new URL(link.href);
            if (linkUrl.pathname === currentAbs.pathname) {
              link.classList.add('active');
            }
          } catch (e) {
          }
        });

        sidetoc.querySelectorAll('.toc .expand-stub').forEach(function (stub) {
          stub.addEventListener('click', function () {
            var parent = stub.parentElement;
            if (parent) {
              parent.classList.toggle('in');
            }
          });
        });

        sidetoc.querySelectorAll('.toc .expand-stub + a:not([href])').forEach(function (anchor) {
          anchor.addEventListener('click', function () {
            var parent = anchor.parentElement;
            if (parent) {
              parent.classList.toggle('in');
            }
          });
        });
      })
      .catch(function () {
      });
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', function () {
      buildNavbar();
      ensureConceptualSidebar();
    });
  } else {
    buildNavbar();
    ensureConceptualSidebar();
  }
})();
