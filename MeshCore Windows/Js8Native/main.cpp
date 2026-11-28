// SPDX-License-Identifier: GPL-3.0-or-later
#include "JS8_Include/commons.h"
#include "JS8_Mode/JS8.h"

#include <QCoreApplication>
#include <QDataStream>
#include <QEventLoop>
#include <QFile>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QLoggingCategory>

#include <algorithm>
#include <array>
#include <cmath>
#include <iostream>
#include <numbers>
#include <stdexcept>
#include <string_view>
#include <vector>

struct dec_data dec_data {};
std::mutex fftw_mutex;
Q_LOGGING_CATEGORY(decoder_js8, "meshcore.js8.decoder", QtWarningMsg)

namespace {
constexpr int protocol = 1;
constexpr auto upstream = "4c592bd9a034f18178a3e7179db92acb14939668";
constexpr int sampleRate = JS8_RX_SAMPLE_RATE;
constexpr auto alphabet = std::string_view("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz-+");

struct Mode {
    int id, mask, samples, delayMs, seconds;
    int decltype(dec_data.params)::*size;
    JS8::Costas::Type sync;
};

constexpr std::array modes{
    Mode{0, 1, JS8A_SYMBOL_SAMPLES, JS8A_START_DELAY_MS, JS8A_TX_SECONDS, &decltype(dec_data.params)::kszA, JS8::Costas::Type::ORIGINAL},
    Mode{1, 2, JS8B_SYMBOL_SAMPLES, JS8B_START_DELAY_MS, JS8B_TX_SECONDS, &decltype(dec_data.params)::kszB, JS8::Costas::Type::MODIFIED},
    Mode{2, 4, JS8C_SYMBOL_SAMPLES, JS8C_START_DELAY_MS, JS8C_TX_SECONDS, &decltype(dec_data.params)::kszC, JS8::Costas::Type::MODIFIED},
    Mode{4, 8, JS8E_SYMBOL_SAMPLES, JS8E_START_DELAY_MS, JS8E_TX_SECONDS, &decltype(dec_data.params)::kszE, JS8::Costas::Type::MODIFIED},
    Mode{8, 16, JS8I_SYMBOL_SAMPLES, JS8I_START_DELAY_MS, JS8I_TX_SECONDS, &decltype(dec_data.params)::kszI, JS8::Costas::Type::MODIFIED},
};

const Mode& selectedMode(const QJsonObject& request)
{
    const int id = request.value("mode").toInt(-1);
    const auto mode = std::ranges::find(modes, id, &Mode::id);
    if (mode == modes.end()) throw std::invalid_argument("Unsupported JS8 mode.");
    return *mode;
}

void wavTag(QDataStream& stream, const char* tag) { stream.writeRawData(tag, 4); }

void encodeWav(const QJsonObject& request, const Mode& mode)
{
    const auto data = request.value("frame").toString().toLatin1();
    const int type = request.value("type").toInt(-1);
    const int frequency = request.value("offsetHz").toInt(-1);
    if (data.size() != 12 || std::ranges::any_of(data, [](char value) { return alphabet.find(value) == std::string_view::npos; }) ||
        type < 0 || type > 7 || frequency < 100 || frequency > 3500)
        throw std::invalid_argument("Expected a 12-character JS8 frame, frame type 0 to 7 and offset 100 to 3500 Hz.");

    std::array<int, JS8_NUM_SYMBOLS> tones{};
    JS8::encode(type, JS8::Costas::array(mode.sync), data.constData(), tones.data());
    std::vector<qint16> samples(mode.seconds * sampleRate);
    int sample = mode.delayMs * sampleRate / 1000;
    double phase = 0;
    for (int tone : tones) {
        const double step = 2 * std::numbers::pi * (frequency / double(sampleRate) + tone / double(mode.samples));
        for (int i = 0; i < mode.samples; ++i) {
            samples.at(sample++) = static_cast<qint16>(12000 * std::sin(phase));
            phase = std::fmod(phase + step, 2 * std::numbers::pi);
        }
    }

    QFile output(request.value("path").toString());
    if (!output.open(QIODevice::WriteOnly | QIODevice::NewOnly))
        throw std::runtime_error("Choose a new WAV file path.");
    QDataStream stream(&output);
    stream.setByteOrder(QDataStream::LittleEndian);
    const auto bytes = quint32(samples.size() * sizeof(qint16));
    wavTag(stream, "RIFF"); stream << quint32(36 + bytes); wavTag(stream, "WAVE");
    wavTag(stream, "fmt "); stream << quint32(16) << quint16(1) << quint16(1)
        << quint32(sampleRate) << quint32(sampleRate * 2) << quint16(2) << quint16(16);
    wavTag(stream, "data"); stream << bytes;
    for (auto value : samples) stream << value;
    if (stream.status() != QDataStream::Ok || !output.flush())
        throw std::runtime_error("Writing the WAV file failed.");
}

int readWav(const QString& path)
{
    QFile input(path);
    if (!input.open(QIODevice::ReadOnly) || input.size() > 2 * JS8_RX_SAMPLE_SIZE + 65'536 ||
        input.read(4) != "RIFF" || !input.seek(8) || input.read(4) != "WAVE")
        throw std::invalid_argument("Choose a RIFF WAV file with at most 60 seconds of 12 kHz mono PCM16.");
    QDataStream stream(&input);
    stream.setByteOrder(QDataStream::LittleEndian);
    bool compatible = false;
    while (input.bytesAvailable() >= 8) {
        const auto tag = input.read(4);
        quint32 bytes = 0;
        stream >> bytes;
        const qint64 start = input.pos();
        if (bytes > input.bytesAvailable()) break;
        if (tag == "fmt " && bytes >= 16) {
            quint16 codec, channels, block, bits;
            quint32 rate, byteRate;
            stream >> codec >> channels >> rate >> byteRate >> block >> bits;
            compatible = codec == 1 && channels == 1 && rate == sampleRate && block == 2 && bits == 16;
        }
        if (tag == "data" && compatible && bytes > 0 && bytes % 2 == 0 && bytes / 2 <= JS8_RX_SAMPLE_SIZE) {
            const int count = int(bytes / 2);
            std::fill(std::begin(dec_data.d2), std::end(dec_data.d2), 0);
            for (int i = 0; i < count; ++i) stream >> dec_data.d2[i];
            if (stream.status() == QDataStream::Ok) return count;
            break;
        }
        if (!input.seek(start + bytes + (bytes & 1))) break;
    }
    throw std::invalid_argument("WAV audio must be 12 kHz mono PCM16.");
}

QJsonArray decodeWav(const QJsonObject& request, const Mode& mode)
{
    const int frequency = request.value("offsetHz").toInt(-1);
    if (frequency < 100 || frequency > 3500) throw std::invalid_argument("Expected an offset from 100 to 3500 Hz.");
    const int count = readWav(request.value("path").toString());
    if (count > mode.seconds * sampleRate)
        throw std::invalid_argument("Supply one slot of audio for the selected mode.");
    dec_data.params = {};
    dec_data.params.nfqso = frequency;
    dec_data.params.nfa = 100;
    dec_data.params.nfb = 3500;
    dec_data.params.newdat = true;
    dec_data.params.kin = mode.seconds * sampleRate;
    dec_data.params.nsubmodes = mode.mask;
    dec_data.params.*mode.size = dec_data.params.kin;

    QJsonArray frames;
    QEventLoop loop;
    JS8::Decoder decoder;
    QObject::connect(&decoder, &JS8::Decoder::decodeEvent, &loop, [&](const JS8::Event::Variant& event) {
        if (const auto* decoded = std::get_if<JS8::Event::Decoded>(&event)) {
            frames.append(QJsonObject{{"frame", QString::fromStdString(decoded->data)}, {"type", decoded->type},
                {"offsetHz", decoded->frequency}, {"snr", decoded->snr}, {"timeOffsetSeconds", decoded->xdt}});
        } else if (std::holds_alternative<JS8::Event::DecodeFinished>(event)) loop.quit();
    });
    decoder.start(QThread::NormalPriority);
    decoder.decode();
    loop.exec();
    decoder.quit();
    return frames;
}

QJsonObject handle(const QJsonObject& request)
{
    if (request.value("protocol").toInt(-1) != protocol) throw std::invalid_argument("Unsupported IPC version.");
    const auto operation = request.value("operation").toString();
    if (operation == "status")
        return {{"upstream", upstream}, {"sampleRate", sampleRate}, {"fileOnly", true}};
    if (operation == "encodeWav") {
        encodeWav(request, selectedMode(request));
        return {};
    }
    if (operation == "decodeWav") return {{"frames", decodeWav(request, selectedMode(request))}};
    throw std::invalid_argument("Unknown operation.");
}
} // namespace

int main(int argc, char** argv)
{
    QCoreApplication app(argc, argv);
    QFile input;
    if (!input.open(stdin, QIODevice::ReadOnly)) return 1;
    while (true) {
        const auto line = input.readLine(8192);
        if (line.isEmpty()) break;
        if (!line.endsWith('\n')) return 2;
        const auto request = QJsonDocument::fromJson(line).object();
        QJsonObject response{{"id", request.value("id")}, {"protocol", protocol}};
        try {
            if (!request.value("id").isString() || !request.contains("operation"))
                throw std::invalid_argument("Expected a JSON request with an ID and operation.");
            response.insert("result", handle(request));
        } catch (const std::exception& error) {
            response.insert("error", QString::fromUtf8(error.what()));
        }
        std::cout << QJsonDocument(response).toJson(QJsonDocument::Compact).constData() << std::endl;
    }
}
