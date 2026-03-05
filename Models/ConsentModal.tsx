// components/ConsentModal.tsx
// Toon deze popup bij eerste app-open als HasConsent = false

import React, { useState } from 'react';
import {
  Modal, View, Text, TouchableOpacity, ScrollView,
  Switch, StyleSheet, ActivityIndicator, Alert,
} from 'react-native';

const API_URL = process.env.EXPO_PUBLIC_API_URL ?? 'https://smartshopper-production.up.railway.app';

interface ConsentModalProps {
  userId: string;
  visible: boolean;
  onConsentGiven: () => void;
}

export default function ConsentModal({ userId, visible, onConsentGiven }: ConsentModalProps) {
  const [analytics, setAnalytics]           = useState(true);
  const [personalization, setPersonalization] = useState(true);
  const [dataSharing, setDataSharing]       = useState(false);
  const [loading, setLoading]               = useState(false);
  const [showDetails, setShowDetails]       = useState(false);

  const handleAccept = async (acceptAll = false) => {
    setLoading(true);
    try {
      const body = {
        userId,
        acceptedTerms: true,
        allowAnalytics: acceptAll ? true : analytics,
        allowPersonalization: acceptAll ? true : personalization,
        allowDataSharing: acceptAll ? true : dataSharing,
        appVersion: '1.0.0',
        deviceType: 'mobile',
        consentVersion: '1.0',
      };

      const res = await fetch(`${API_URL}/api/consent`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      });

      if (res.ok) {
        onConsentGiven();
      } else {
        Alert.alert('Fout', 'Kon toestemming niet opslaan. Probeer het opnieuw.');
      }
    } catch {
      Alert.alert('Fout', 'Geen verbinding. Probeer het opnieuw.');
    } finally {
      setLoading(false);
    }
  };

  const handleDeclineAll = async () => {
    // Gebruiker accepteert alleen verplichte voorwaarden, geen extras
    setLoading(true);
    try {
      await fetch(`${API_URL}/api/consent`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          userId,
          acceptedTerms: true,
          allowAnalytics: false,
          allowPersonalization: false,
          allowDataSharing: false,
          appVersion: '1.0.0',
          deviceType: 'mobile',
          consentVersion: '1.0',
        }),
      });
      onConsentGiven();
    } catch {
      Alert.alert('Fout', 'Geen verbinding. Probeer het opnieuw.');
    } finally {
      setLoading(false);
    }
  };

  return (
    <Modal visible={visible} animationType="slide" transparent presentationStyle="overFullScreen">
      <View style={styles.overlay}>
        <View style={styles.container}>
          <ScrollView showsVerticalScrollIndicator={false}>

            {/* Header */}
            <Text style={styles.emoji}>🛒</Text>
            <Text style={styles.title}>Welkom bij SmartShopper</Text>
            <Text style={styles.subtitle}>
              Voordat je begint, willen we je informeren over hoe we jouw gegevens gebruiken.
            </Text>

            {/* Altijd verplicht */}
            <View style={styles.section}>
              <View style={styles.row}>
                <View style={styles.textWrap}>
                  <Text style={styles.optionTitle}>✅ Gebruiksvoorwaarden</Text>
                  <Text style={styles.optionDesc}>
                    Noodzakelijk voor het gebruik van de app. Bevat technische logging
                    en beveiliging.
                  </Text>
                </View>
                <Switch value={true} disabled thumbColor="#fff" trackColor={{ true: '#4CAF50' }} />
              </View>
            </View>

            {/* Optionele toestemmingen */}
            <Text style={styles.sectionTitle}>Optioneel — jij kiest:</Text>

            <View style={styles.section}>
              <View style={styles.row}>
                <View style={styles.textWrap}>
                  <Text style={styles.optionTitle}>📊 Analyse & Verbetering</Text>
                  <Text style={styles.optionDesc}>
                    We slaan op welke producten je vergelijkt en welke winkels je kiest.
                    Dit helpt ons de app te verbeteren en betere deals te vinden.
                  </Text>
                </View>
                <Switch
                  value={analytics}
                  onValueChange={setAnalytics}
                  thumbColor="#fff"
                  trackColor={{ true: '#2196F3', false: '#ccc' }}
                />
              </View>
            </View>

            <View style={styles.section}>
              <View style={styles.row}>
                <View style={styles.textWrap}>
                  <Text style={styles.optionTitle}>🎯 Personalisatie</Text>
                  <Text style={styles.optionDesc}>
                    We onthouden jouw aankoophistorie en voorkeuren voor persoonlijke
                    aanbevelingen en aanbiedingen op maat.
                  </Text>
                </View>
                <Switch
                  value={personalization}
                  onValueChange={setPersonalization}
                  thumbColor="#fff"
                  trackColor={{ true: '#2196F3', false: '#ccc' }}
                />
              </View>
            </View>

            <View style={styles.section}>
              <View style={styles.row}>
                <View style={styles.textWrap}>
                  <Text style={styles.optionTitle}>🤝 Geanonimiseerd delen</Text>
                  <Text style={styles.optionDesc}>
                    Jouw (volledig geanonimiseerde) winkeldata kan worden gedeeld met
                    onderzoekspartners. Nooit herleidbaar naar jou persoonlijk.
                  </Text>
                </View>
                <Switch
                  value={dataSharing}
                  onValueChange={setDataSharing}
                  thumbColor="#fff"
                  trackColor={{ true: '#2196F3', false: '#ccc' }}
                />
              </View>
            </View>

            {/* Details toggle */}
            <TouchableOpacity onPress={() => setShowDetails(!showDetails)}>
              <Text style={styles.detailsToggle}>
                {showDetails ? '▲ Minder info' : '▼ Meer informatie'}
              </Text>
            </TouchableOpacity>

            {showDetails && (
              <View style={styles.details}>
                <Text style={styles.detailsText}>
                  • Je kunt je toestemming altijd intrekken via Instellingen → Privacy{'\n'}
                  • Wij verkopen nooit herleidbare persoonsgegevens{'\n'}
                  • Je hebt recht op inzage, correctie en verwijdering (AVG/GDPR){'\n'}
                  • Gegevens worden opgeslagen in de EU (Supabase EU-regio){'\n'}
                  • Volledige privacyverklaring: smartshopper.app/privacy
                </Text>
              </View>
            )}

            {/* Knoppen */}
            <View style={styles.buttons}>
              {loading ? (
                <ActivityIndicator size="large" color="#2196F3" style={{ marginVertical: 20 }} />
              ) : (
                <>
                  <TouchableOpacity style={styles.btnPrimary} onPress={() => handleAccept(false)}>
                    <Text style={styles.btnPrimaryText}>Opslaan & doorgaan</Text>
                  </TouchableOpacity>

                  <TouchableOpacity style={styles.btnSecondary} onPress={() => handleAccept(true)}>
                    <Text style={styles.btnSecondaryText}>Alles accepteren</Text>
                  </TouchableOpacity>

                  <TouchableOpacity style={styles.btnGhost} onPress={handleDeclineAll}>
                    <Text style={styles.btnGhostText}>Alleen noodzakelijk</Text>
                  </TouchableOpacity>
                </>
              )}
            </View>

          </ScrollView>
        </View>
      </View>
    </Modal>
  );
}

const styles = StyleSheet.create({
  overlay: {
    flex: 1, backgroundColor: 'rgba(0,0,0,0.6)',
    justifyContent: 'flex-end',
  },
  container: {
    backgroundColor: '#fff', borderTopLeftRadius: 24, borderTopRightRadius: 24,
    padding: 24, maxHeight: '90%',
  },
  emoji:      { fontSize: 40, textAlign: 'center', marginBottom: 8 },
  title:      { fontSize: 22, fontWeight: '700', textAlign: 'center', color: '#111', marginBottom: 8 },
  subtitle:   { fontSize: 14, color: '#666', textAlign: 'center', marginBottom: 20, lineHeight: 20 },
  sectionTitle: { fontSize: 13, fontWeight: '600', color: '#888', marginBottom: 8, marginTop: 4 },
  section: {
    backgroundColor: '#f8f9fa', borderRadius: 12, padding: 14, marginBottom: 10,
  },
  row:        { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between' },
  textWrap:   { flex: 1, paddingRight: 12 },
  optionTitle: { fontSize: 15, fontWeight: '600', color: '#222', marginBottom: 4 },
  optionDesc: { fontSize: 13, color: '#666', lineHeight: 18 },
  detailsToggle: { color: '#2196F3', textAlign: 'center', marginVertical: 12, fontSize: 13 },
  details: {
    backgroundColor: '#f0f4ff', borderRadius: 10, padding: 14, marginBottom: 12,
  },
  detailsText: { fontSize: 13, color: '#444', lineHeight: 20 },
  buttons:    { marginTop: 8, gap: 10 },
  btnPrimary: {
    backgroundColor: '#2196F3', borderRadius: 14, paddingVertical: 16,
    alignItems: 'center',
  },
  btnPrimaryText: { color: '#fff', fontSize: 16, fontWeight: '700' },
  btnSecondary: {
    backgroundColor: '#E3F2FD', borderRadius: 14, paddingVertical: 14,
    alignItems: 'center',
  },
  btnSecondaryText: { color: '#2196F3', fontSize: 15, fontWeight: '600' },
  btnGhost: {
    borderRadius: 14, paddingVertical: 12, alignItems: 'center',
  },
  btnGhostText: { color: '#999', fontSize: 14 },
});
