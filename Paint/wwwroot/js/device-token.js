(function () {
    const KEY = 'paint_dt';
    let token = localStorage.getItem(KEY);
    if (!token) {
        const arr = new Uint8Array(32);
        crypto.getRandomValues(arr);
        token = Array.from(arr, b => b.toString(16).padStart(2, '0')).join('');
        localStorage.setItem(KEY, token);
        document.cookie = KEY + '=' + token + '; max-age=' + (60 * 60 * 24 * 365) + '; path=/; SameSite=Lax';
    }
    document.querySelectorAll('input[name="DeviceToken"]').forEach(el => el.value = token);
})();
