const mode=document.querySelector('#imdb-mode');
function updateMode(){document.querySelectorAll('[data-imdb-mode]').forEach(section=>{section.hidden=section.dataset.imdbMode!==mode.value;});}
mode?.addEventListener('change',updateMode);
if(mode)updateMode();
