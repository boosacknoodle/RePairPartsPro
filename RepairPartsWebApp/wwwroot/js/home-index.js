(() => {
    const searchForm = document.getElementById('searchForm');
    const resultsDiv = document.getElementById('results');
    const healthDot = document.getElementById('healthDot');
    const healthText = document.getElementById('healthText');

    const lightbox = document.getElementById('lightbox');
    const lightboxImage = document.getElementById('lightboxImage');
    const lightboxCount = document.getElementById('lightboxCount');
    const lightboxClose = document.getElementById('lightboxClose');
    const lightboxPrev = document.getElementById('lightboxPrev');
    const lightboxNext = document.getElementById('lightboxNext');

    const marketplaceFilter = document.getElementById('marketplaceFilter');
    const minFeedbackFilter = document.getElementById('minFeedbackFilter');
    const minScoreFilter = document.getElementById('minScoreFilter');
    const bestOnlyFilter = document.getElementById('bestOnlyFilter');
    const viewCardsBtn = document.getElementById('viewCardsBtn');
    const viewTableBtn = document.getElementById('viewTableBtn');
    const billingEmailInput = document.getElementById('billingEmail');
    const billingStatus = document.getElementById('billingStatus');
    const planList = document.getElementById('planList');

    let currentView = 'cards';
    let currentCustomerId = Number(document.getElementById('customerId')?.value || 1);
    let lastResult = null;

    function setBillingStatus(message, isError = false) {
        if (!billingStatus) {
            return;
        }

        billingStatus.textContent = message;
        billingStatus.style.color = isError ? '#fecaca' : '';
    }

    function escapeHtml(value) {
        return String(value || '')
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#039;');
    }

    function formatUsd(value) {
        const amount = Number(value || 0);
        return amount.toLocaleString('en-US', { style: 'currency', currency: 'USD' });
    }

    async function startCheckout(planKey, button) {
        const customerId = Number(document.getElementById('customerId')?.value || 1);
        if (customerId <= 0) {
            setBillingStatus('Enter a valid Customer ID before subscribing.', true);
            return;
        }

        const email = (billingEmailInput?.value || '').trim();
        if (!email) {
            setBillingStatus('Billing email is required for checkout.', true);
            billingEmailInput?.focus();
            return;
        }

        button.disabled = true;
        const originalLabel = button.textContent;
        button.textContent = 'Creating checkout...';
        setBillingStatus('Creating secure checkout session...');

        try {
            const baseUrl = window.location.origin;
            const response = await fetch('/api/billing/checkout-session', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    customerId,
                    email,
                    planKey,
                    successUrl: `${baseUrl}/?billing=success`,
                    cancelUrl: `${baseUrl}/?billing=cancel`
                })
            });

            if (!response.ok) {
                let message = 'Unable to create checkout session.';
                try {
                    const error = await response.json();
                    if (error?.message) {
                        message = error.message;
                    }
                } catch {
                    // no-op: keep fallback message
                }

                throw new Error(message);
            }

            const data = await response.json();
            if (!data?.url) {
                throw new Error('Checkout URL was not returned by the billing API.');
            }

            setBillingStatus('Redirecting to Stripe checkout...');
            window.location.href = data.url;
        } catch (error) {
            setBillingStatus(error?.message || 'Checkout failed. Please retry.', true);
        } finally {
            button.disabled = false;
            button.textContent = originalLabel;
        }
    }

    function bindPlanButtons() {
        document.querySelectorAll('.plan-btn').forEach((button) => {
            button.addEventListener('click', async () => {
                const planKey = button.getAttribute('data-plan-key') || '';
                if (!planKey) {
                    setBillingStatus('Invalid plan selected.', true);
                    return;
                }

                await startCheckout(planKey, button);
            });
        });
    }

    function renderPlans(plans) {
        if (!planList) {
            return;
        }

        if (!Array.isArray(plans) || plans.length === 0) {
            planList.innerHTML = '<div class="empty">No active plans configured.</div>';
            return;
        }

        const html = plans.map((plan) => {
            const key = escapeHtml(plan.key);
            const name = escapeHtml(plan.name);
            const interval = escapeHtml(plan.interval || 'month');
            return `
                <article class="plan-card">
                    <div class="plan-name">${name}</div>
                    <div class="plan-meta">Plan key: ${key}</div>
                    <div class="plan-price">${formatUsd(plan.amountUsd)}/${interval}</div>
                    <button class="plan-btn" type="button" data-plan-key="${key}">Subscribe</button>
                </article>`;
        }).join('');

        planList.innerHTML = html;
        bindPlanButtons();
    }

    async function initializeBilling() {
        if (!planList || !billingStatus) {
            return;
        }

        setBillingStatus('Checking billing readiness...');

        try {
            const readinessResponse = await fetch('/api/billing/readiness', { cache: 'no-store' });
            if (readinessResponse.ok) {
                const readiness = await readinessResponse.json();
                if (Array.isArray(readiness?.issues) && readiness.issues.length > 0) {
                    setBillingStatus(`Billing setup needs attention: ${readiness.issues[0]}`, true);
                } else {
                    setBillingStatus('Billing ready. Choose a plan to subscribe.');
                }
            }
        } catch {
            setBillingStatus('Billing readiness check unavailable. Attempting plan load...');
        }

        try {
            const plansResponse = await fetch('/api/billing/plans', { cache: 'no-store' });
            if (!plansResponse.ok) {
                throw new Error('Failed to load billing plans.');
            }

            const plans = await plansResponse.json();
            renderPlans(plans);

            if (!billingStatus.textContent || billingStatus.textContent.includes('Checking')) {
                setBillingStatus('Billing ready. Choose a plan to subscribe.');
            }
        } catch (error) {
            renderPlans([]);
            setBillingStatus(error?.message || 'Unable to load billing plans.', true);
        }
    }

    async function refreshHealth() {
        try {
            const response = await fetch('/health', { cache: 'no-store' });
            const data = await response.json();

            if (response.ok && data.status === 'ok') {
                healthDot.className = 'status-dot ok';
                healthText.textContent = `API Healthy • DB Up • ${data.partCount} parts`;
            } else {
                healthDot.className = 'status-dot warn';
                healthText.textContent = 'Service degraded • Check API/DB';
            }
        } catch {
            healthDot.className = 'status-dot down';
            healthText.textContent = 'Service unavailable';
        }
    }

    function applyListingFilters(listings) {
        const minFeedback = Number(minFeedbackFilter.value || 0);
        const minScore = Number(minScoreFilter.value || 0);
        const market = marketplaceFilter.value || 'all';
        const bestOnly = bestOnlyFilter.checked;

        return listings.filter((listing) => {
            if (market !== 'all' && listing.marketplace !== market) {
                return false;
            }

            if (Number(listing.sellerFeedback || 0) < minFeedback) {
                return false;
            }

            if (Number(listing.score || 0) < minScore) {
                return false;
            }

            if (bestOnly && !listing.isBestPrice) {
                return false;
            }

            return true;
        });
    }

    function getFallbackImage(listingTitle, index) {
        const titleLower = (listingTitle || '').toLowerCase();
        const slot = (index % 4) + 1;

        if (titleLower.includes('battery')) return `/images/parts/battery-${slot}.jpg`;
        if (titleLower.includes('ssd') || titleLower.includes('nvme')) return `/images/parts/ssd-${slot}.jpg`;
        if (titleLower.includes('motherboard')) return `/images/parts/motherboard-${slot}.jpg`;
        if (titleLower.includes('ram') || titleLower.includes('ddr')) return `/images/parts/ram-${slot}.jpg`;
        if (titleLower.includes('cooler')) return `/images/parts/cpu-cooler-${slot}.jpg`;
        if (titleLower.includes('keyboard')) return `/images/parts/keyboard-${slot}.jpg`;
        if (titleLower.includes('mouse')) return `/images/parts/mouse-${slot}.jpg`;
        if (titleLower.includes('power supply') || titleLower.includes('psu')) return `/images/parts/power-supply-${slot}.jpg`;

        return `/images/parts/default-${(index % 3) + 1}.jpg`;
    }

    function renderTableView(listings) {
        let html = `
            <div class="table-wrap">
                <table class="result-table">
                    <thead>
                        <tr>
                            <th>Image</th>
                            <th>Part</th>
                            <th>Marketplace</th>
                            <th>Price</th>
                            <th>Seller</th>
                            <th>Feedback</th>
                            <th>Score</th>
                            <th>Actions</th>
                        </tr>
                    </thead>
                    <tbody>`;

        listings.forEach((listing, index) => {
            const partNumbers = Array.isArray(listing.partNumbers) ? listing.partNumbers.join(' • ') : listing.partNumbers;
            const safeImage = listing.imageUrl && listing.imageUrl.trim().length > 0
                ? listing.imageUrl
                : getFallbackImage(listing.title, index);
            const listingUrl = listing.listingUrl && listing.listingUrl.trim().length > 0 ? listing.listingUrl : '#';
            const safeTitle = (listing.title || '').replace(/"/g, '&quot;');
            const safeSeller = (listing.sellerName || '').replace(/"/g, '&quot;');
            const priceLabel = listing.isEstimatedPrice ? `~$${listing.price.toFixed(2)}` : `$${listing.price.toFixed(2)}`;

            html += `
                <tr>
                    <td><img class="table-thumb" src="${safeImage}" alt="${safeTitle}" /></td>
                    <td><strong>${listing.title}</strong><br /><span style="color:#9db6df">${partNumbers}</span></td>
                    <td>${listing.marketplace}</td>
                    <td>${priceLabel}${listing.isEstimatedPrice ? ' <span style="color:#93c5fd; font-size:0.8rem;">(est.)</span>' : ''}</td>
                    <td>${listing.sellerName}</td>
                    <td>${Number(listing.sellerFeedback).toFixed(1)}%</td>
                    <td>${listing.score}/5.0</td>
                    <td>
                        <a class="view-details-btn" href="${listingUrl}" target="_blank" rel="noopener noreferrer">Open</a>
                        <button class="blacklist-btn" data-seller="${safeSeller}" data-title="${safeTitle}" data-part="${partNumbers.replace(/"/g, '&quot;')}">Report</button>
                    </td>
                </tr>`;
        });

        html += '</tbody></table></div>';
        return html;
    }

    function renderCardView(listings) {
        const galleries = {};
        let html = '';

        listings.forEach((listing, index) => {
            const badgeColor = listing.marketplace === 'Amazon' ? '#f59e0b' : '#e11d48';
            const isBestPrice = listing.isBestPrice;
            const reasons = Array.isArray(listing.reasons) ? listing.reasons.join(' • ') : listing.reasons;
            const partNumbers = Array.isArray(listing.partNumbers) ? listing.partNumbers.join(' • ') : listing.partNumbers;
            const fallbackImage = getFallbackImage(listing.title, index);
            const safeImage = listing.imageUrl && listing.imageUrl.trim().length > 0 ? listing.imageUrl : fallbackImage;
            const imageList = Array.isArray(listing.imageUrls) && listing.imageUrls.length > 0 ? listing.imageUrls : [safeImage];
            const galleryId = `gallery-${index}`;
            galleries[galleryId] = imageList;
            const listingUrl = listing.listingUrl && listing.listingUrl.trim().length > 0 ? listing.listingUrl : '#';
            const safeTitle = (listing.title || '').replace(/"/g, '&quot;');
            const safeSeller = (listing.sellerName || '').replace(/"/g, '&quot;');
            const priceLabel = listing.isEstimatedPrice ? `~$${listing.price.toFixed(2)}` : `$${listing.price.toFixed(2)}`;

            html += `
                <article class="card" style="animation-delay:${Math.min(index * 45, 400)}ms;">
                    <div class="card-content">
                        <div class="image-wrap">
                            <button class="img-nav left" data-gallery="${galleryId}" data-dir="-1" ${imageList.length > 1 ? '' : 'disabled'} aria-label="Previous image">&#10094;</button>
                            <img class="part-image" id="img-${galleryId}" src="${imageList[0]}" alt="${safeTitle}" loading="lazy" onerror="this.style.display='none'; this.parentElement.querySelector('.part-image-fallback').style.display='flex';" />
                            <button class="img-nav right" data-gallery="${galleryId}" data-dir="1" ${imageList.length > 1 ? '' : 'disabled'} aria-label="Next image">&#10095;</button>
                            <div class="img-count" id="count-${galleryId}">1/${imageList.length}</div>
                            <div class="part-image-fallback" style="display:none;">NO IMAGE</div>
                        </div>
                        <div>
                            <div class="card-title-row">
                                <div class="card-title">[PART_${index + 1}] ${listing.title}</div>
                                <span class="market-badge" style="background:${badgeColor};">${listing.marketplace}</span>
                            </div>
                            <div class="price">${priceLabel}${listing.isEstimatedPrice ? ' <span style="color:#93c5fd; font-size:0.85rem;">(estimated)</span>' : ''}</div>
                            ${!isBestPrice ? `<div class="best">Lowest: $${listing.bestPrice.toFixed(2)} on ${listing.bestPriceMarketplace}</div>` : '<div class="best">This is the lowest available price.</div>'}
                            <div class="seller"><strong>Vendor:</strong> ${listing.sellerName} | <strong>Feedback:</strong> ${Number(listing.sellerFeedback).toFixed(1)}% | <strong>Score:</strong> ${listing.score}/5.0</div>
                            <div class="meta"><strong>Part Number:</strong> ${partNumbers}</div>
                            <div class="meta"><strong>Verification:</strong> ${reasons}</div>
                            <a class="view-details-btn" href="${listingUrl}" target="_blank" rel="noopener noreferrer">View Listing</a>
                            <button class="blacklist-btn" data-seller="${safeSeller}" data-title="${safeTitle}" data-part="${partNumbers.replace(/"/g, '&quot;')}">Report suspected scam</button>
                        </div>
                    </div>
                </article>`;
        });

        return { html, galleries };
    }

    function bindCardInteractions(galleries) {
        const galleryState = {};
        Object.keys(galleries).forEach((galleryId) => {
            galleryState[galleryId] = 0;
        });

        const lightboxState = { galleryId: null, index: 0 };

        function renderLightbox() {
            if (!lightboxState.galleryId) {
                return;
            }

            const images = galleries[lightboxState.galleryId] || [];
            if (images.length === 0) {
                return;
            }

            const idx = ((lightboxState.index % images.length) + images.length) % images.length;
            lightboxState.index = idx;
            lightboxImage.src = images[idx];
            lightboxCount.textContent = `${idx + 1}/${images.length}`;
        }

        function openLightbox(galleryId) {
            lightboxState.galleryId = galleryId;
            lightboxState.index = galleryState[galleryId] || 0;
            lightbox.classList.add('open');
            lightbox.setAttribute('aria-hidden', 'false');
            renderLightbox();
        }

        function closeLightbox() {
            lightbox.classList.remove('open');
            lightbox.setAttribute('aria-hidden', 'true');
            lightboxImage.removeAttribute('src');
            lightboxState.galleryId = null;
        }

        document.querySelectorAll('.img-nav').forEach((button) => {
            button.addEventListener('click', () => {
                const galleryId = button.getAttribute('data-gallery');
                const dir = parseInt(button.getAttribute('data-dir') || '0', 10);
                const images = galleries[galleryId] || [];
                if (images.length <= 1 || !galleryId || dir === 0) {
                    return;
                }

                const nextIndex = (galleryState[galleryId] + dir + images.length) % images.length;
                galleryState[galleryId] = nextIndex;

                const imageEl = document.getElementById(`img-${galleryId}`);
                const countEl = document.getElementById(`count-${galleryId}`);
                if (imageEl) {
                    imageEl.style.display = '';
                    imageEl.src = images[nextIndex];
                }

                const fallbackEl = imageEl?.parentElement?.querySelector('.part-image-fallback');
                if (fallbackEl) {
                    fallbackEl.style.display = 'none';
                }

                if (countEl) {
                    countEl.textContent = `${nextIndex + 1}/${images.length}`;
                }
            });
        });

        document.querySelectorAll('.part-image').forEach((imageEl) => {
            imageEl.style.cursor = 'zoom-in';
            imageEl.addEventListener('click', () => {
                const id = imageEl.id || '';
                const galleryId = id.replace('img-', '');
                if (galleryId) {
                    openLightbox(galleryId);
                }
            });
        });

        lightboxClose.onclick = closeLightbox;
        lightboxPrev.onclick = () => {
            if (!lightboxState.galleryId) return;
            lightboxState.index -= 1;
            renderLightbox();
        };
        lightboxNext.onclick = () => {
            if (!lightboxState.galleryId) return;
            lightboxState.index += 1;
            renderLightbox();
        };

        lightbox.onclick = (event) => {
            if (event.target === lightbox) {
                closeLightbox();
            }
        };

        document.onkeydown = (event) => {
            if (!lightbox.classList.contains('open')) {
                return;
            }

            if (event.key === 'Escape') {
                closeLightbox();
            } else if (event.key === 'ArrowLeft') {
                lightboxState.index -= 1;
                renderLightbox();
            } else if (event.key === 'ArrowRight') {
                lightboxState.index += 1;
                renderLightbox();
            }
        };
    }

    function bindBlacklistButtons() {
        document.querySelectorAll('.blacklist-btn').forEach((button) => {
            button.addEventListener('click', async () => {
                const sellerName = button.getAttribute('data-seller');
                const listingTitle = button.getAttribute('data-title') || '';
                const expectedPart = button.getAttribute('data-part') || '';

                const receivedWrongItem = confirm('Objective check 1: Did you receive a wrong item/part? Click OK for Yes, Cancel for No.');
                const counterfeitPackaging = confirm('Objective check 2: Did the packaging or serial look counterfeit/tampered? OK=Yes, Cancel=No.');
                const listingMismatch = confirm('Objective check 3: Does the delivered part mismatch listing photos/specs? OK=Yes, Cancel=No.');
                const sellerRefusedReturn = confirm('Objective check 4: Did seller refuse a valid return or proof request? OK=Yes, Cancel=No.');

                const receivedPartNumber = prompt('Enter RECEIVED part number/serial from item (required for objective validation):', '');
                if (receivedPartNumber === null) {
                    return;
                }

                const evidenceNotes = prompt('Enter concise evidence notes (what happened, timeline, proof details). Minimum ~20 chars:', '');
                if (evidenceNotes === null || evidenceNotes.trim().length < 20) {
                    alert('Evidence notes too short. Please include objective details before submitting a scam report.');
                    return;
                }

                const evidenceUrl = prompt('Optional: paste proof URL (photos/video/chat screenshot link).', '') || '';

                try {
                    const reviewResponse = await fetch('/api/parts/blacklist/review', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({
                            customerId: Number(currentCustomerId),
                            sellerName,
                            listingTitle,
                            expectedPartNumber: expectedPart,
                            receivedPartNumber: receivedPartNumber.trim(),
                            receivedWrongItem,
                            counterfeitPackaging,
                            listingMismatch,
                            sellerRefusedReturn,
                            evidenceNotes: evidenceNotes.trim(),
                            evidenceUrl: evidenceUrl.trim()
                        })
                    });

                    if (!reviewResponse.ok) {
                        throw new Error('Unable to submit review.');
                    }

                    const review = await reviewResponse.json();
                    const msg = `${review.message}\nConfidence score: ${review.confidenceScore}%\nPoints awarded: ${review.pointsAwarded}\nTotal negative points: ${review.totalNegativePoints}/${review.globalBlacklistThreshold}\nDecision: ${review.decision}`;
                    alert(msg);

                    searchForm.dispatchEvent(new Event('submit'));
                } catch {
                    alert('Failed to submit scam review. Please try again.');
                }
            });
        });
    }

    function renderResult(result) {
        if (!result || !Array.isArray(result.listings)) {
            resultsDiv.innerHTML = '<div class="empty">No matching parts found.</div>';
            return;
        }

        const filteredListings = applyListingFilters(result.listings);
        if (filteredListings.length === 0) {
            resultsDiv.innerHTML = '<div class="empty">No listings match the current filters.</div>';
            return;
        }

        let html = `
            <div class="results-header">
                <h2>Search Results</h2>
                <p>Showing ${filteredListings.length} of ${result.listings.length} listing(s) for ${result.brand} ${result.model}</p>
            </div>`;

        if (currentView === 'table') {
            html += renderTableView(filteredListings);
            resultsDiv.innerHTML = html;
            bindBlacklistButtons();
            return;
        }

        const cardView = renderCardView(filteredListings);
        html += cardView.html;
        resultsDiv.innerHTML = html;

        bindCardInteractions(cardView.galleries);
        bindBlacklistButtons();
    }

    async function runSearch() {
        const customerIdInput = document.getElementById('customerId');
        const brandInput = document.getElementById('brand');
        const modelInput = document.getElementById('model');
        const partTypeInput = document.getElementById('partType');

        currentCustomerId = Number(customerIdInput.value || 1);
        const brand = brandInput.value;
        const model = modelInput.value;
        const partType = partTypeInput.value;

        resultsDiv.innerHTML = '<div class="loading">Scanning listings...</div>';

        try {
            const response = await fetch('/api/parts/search', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    customerId: currentCustomerId,
                    brand,
                    model,
                    partType
                })
            });

            if (!response.ok) {
                throw new Error('Search failed');
            }

            lastResult = await response.json();
            renderResult(lastResult);
        } catch {
            resultsDiv.innerHTML = '<div class="error">Unable to load results. Please try again.</div>';
        }
    }

    searchForm.addEventListener('submit', async (e) => {
        e.preventDefault();
        await runSearch();
    });

    [marketplaceFilter, minFeedbackFilter, minScoreFilter, bestOnlyFilter].forEach((control) => {
        control.addEventListener('change', () => {
            if (lastResult) {
                renderResult(lastResult);
            }
        });
    });

    viewCardsBtn.addEventListener('click', () => {
        currentView = 'cards';
        viewCardsBtn.classList.add('active');
        viewTableBtn.classList.remove('active');
        if (lastResult) {
            renderResult(lastResult);
        }
    });

    viewTableBtn.addEventListener('click', () => {
        currentView = 'table';
        viewTableBtn.classList.add('active');
        viewCardsBtn.classList.remove('active');
        if (lastResult) {
            renderResult(lastResult);
        }
    });

    refreshHealth();
    initializeBilling();
    runSearch();
    setInterval(refreshHealth, 30000);
})();
