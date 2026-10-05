/* MusicMachine audio-only FFmpeg adapter. Copyright 2026 MusicMachine contributors.
 * SPDX-License-Identifier: MIT
 * Linked FFmpeg code remains LGPL-2.1-or-later; see the source bundle and notices.
 */
#include <stdint.h>
#include <stddef.h>
#include <stdio.h>
#include <string.h>
#include <errno.h>
#include <emscripten.h>
#include <libavformat/avformat.h>
#include <libavcodec/avcodec.h>
#include <libavutil/channel_layout.h>
#include <libavutil/mem.h>
#include <libavutil/log.h>
#include <libswresample/swresample.h>

#define MAX_INPUT (32 * 1024 * 1024)
#define MAX_FRAMES (30 * 48000)
#define MAX_RATE 192000
#define MAX_CHANNELS 8
/* -1 invalid audio, -2 size, -3 duration, -4 format, -5 memory, -6 timeout. */
static int16_t output[MAX_FRAMES + 1];
static int output_frames;
static double deadline;
typedef struct { const uint8_t *data; int size; int position; } Input;
typedef struct {
    SwrContext *swr;
    AVChannelLayout layout;
    int rate;
    int format;
} Resampler;

EM_JS(void, mm_progress, (int frames), {
    if (Module.onProgress) Module.onProgress(frames / 1440000);
});
static int timed_out(void *unused) { (void)unused; return emscripten_get_now() > deadline; }
static int read_packet(void *opaque, uint8_t *buffer, int size) {
    Input *in = opaque;
    if (timed_out(NULL)) return AVERROR_EXIT;
    if (in->position >= in->size) return AVERROR_EOF;
    if (size > in->size - in->position) size = in->size - in->position;
    memcpy(buffer, in->data + in->position, size); in->position += size; return size;
}
static int64_t seek_packet(void *opaque, int64_t offset, int whence) {
    Input *in = opaque;
    if (whence == AVSEEK_SIZE) return in->size;
    whence &= ~AVSEEK_FORCE;
    int64_t base = whence == SEEK_SET ? 0 : whence == SEEK_CUR ? in->position : whence == SEEK_END ? in->size : -1;
    if (base < 0 || offset < -base || offset > in->size - base) return AVERROR(EINVAL);
    in->position = (int)(base + offset); return in->position;
}
static int receive_frame(Resampler *r, AVFrame *frame) {
    if (frame->sample_rate < 1 || frame->sample_rate > MAX_RATE ||
        frame->ch_layout.nb_channels < 1 || frame->ch_layout.nb_channels > MAX_CHANNELS) return -4;
    if (!r->swr) {
        AVChannelLayout mono = AV_CHANNEL_LAYOUT_MONO;
        if (av_channel_layout_copy(&r->layout, &frame->ch_layout) < 0) return -5;
        r->rate = frame->sample_rate; r->format = frame->format;
        if (swr_alloc_set_opts2(&r->swr, &mono, AV_SAMPLE_FMT_S16, 48000,
                &frame->ch_layout, frame->format, frame->sample_rate, 0, NULL) < 0) return -5;
        double matrix[MAX_CHANNELS];
        for (int i = 0; i < frame->ch_layout.nb_channels; i++) matrix[i] = 1.0 / frame->ch_layout.nb_channels;
        if (swr_set_matrix(r->swr, matrix, frame->ch_layout.nb_channels) < 0 || swr_init(r->swr) < 0) return -4;
    } else if (r->rate != frame->sample_rate || r->format != frame->format ||
               av_channel_layout_compare(&r->layout, &frame->ch_layout)) return -4;
    uint8_t *dest[] = { (uint8_t *)(output + output_frames) };
    int count = swr_convert(r->swr, dest, MAX_FRAMES + 1 - output_frames,
                           (const uint8_t **)frame->extended_data, frame->nb_samples);
    if (count < 0) return -1;
    output_frames += count;
    if (output_frames > MAX_FRAMES) return -3;
    mm_progress(output_frames);
    return 0;
}
static int drain(AVCodecContext *codec, AVFrame *frame, Resampler *r) {
    while (!timed_out(NULL)) {
        int status = avcodec_receive_frame(codec, frame);
        if (status == AVERROR(EAGAIN) || status == AVERROR_EOF) return 0;
        if (status < 0) return -1;
        status = receive_frame(r, frame); av_frame_unref(frame);
        if (status) return status;
    }
    return -6;
}
EMSCRIPTEN_KEEPALIVE const char *mm_version(void) { return av_version_info(); }
EMSCRIPTEN_KEEPALIVE const char *mm_license(void) { return avcodec_license(); }
EMSCRIPTEN_KEEPALIVE const char *mm_configuration(void) { return avcodec_configuration(); }
EMSCRIPTEN_KEEPALIVE const int16_t *mm_pcm(void) { return output; }
EMSCRIPTEN_KEEPALIVE int mm_decode(const uint8_t *bytes, int length) {
    output_frames = 0; deadline = emscripten_get_now() + 110000;
    if (!bytes || length < 1 || length > MAX_INPUT) return -2;
    av_log_set_level(AV_LOG_QUIET); av_max_alloc(64 * 1024 * 1024);
    Input input = { bytes, length, 0 };
    AVFormatContext *format = avformat_alloc_context();
    AVIOContext *io = NULL;
    AVCodecContext *codec = NULL;
    AVPacket *packet = NULL;
    AVFrame *frame = NULL;
    Resampler resampler = {0};
    int status = -5;
    uint8_t *buffer = av_malloc(32768);
    if (!format || !buffer) { av_free(buffer); goto cleanup; }
    io = avio_alloc_context(buffer, 32768, 0, &input, read_packet, NULL, seek_packet);
    if (!io) { av_free(buffer); goto cleanup; }
    format->pb = io; format->flags |= AVFMT_FLAG_CUSTOM_IO;
    format->probesize = 8 * 1024 * 1024; format->max_analyze_duration = 10 * AV_TIME_BASE;
    format->max_streams = 16; format->max_probe_packets = 64;
    format->interrupt_callback = (AVIOInterruptCB){ timed_out, NULL };
    status = -1;
    if (avformat_open_input(&format, NULL, NULL, NULL) < 0 || avformat_find_stream_info(format, NULL) < 0) goto cleanup;
    int stream = -1;
    for (unsigned i = 0; i < format->nb_streams; i++)
        if (format->streams[i]->codecpar->codec_type == AVMEDIA_TYPE_AUDIO) { stream = (int)i; break; }
    if (stream < 0) goto cleanup;
    const AVCodecParameters *parameters = format->streams[stream]->codecpar;
    status = -4;
    if (parameters->sample_rate < 1 || parameters->sample_rate > MAX_RATE ||
        parameters->ch_layout.nb_channels < 1 || parameters->ch_layout.nb_channels > MAX_CHANNELS) goto cleanup;
    const AVCodec *decoder = avcodec_find_decoder(parameters->codec_id);
    if (!decoder) goto cleanup;
    codec = avcodec_alloc_context3(decoder); packet = av_packet_alloc(); frame = av_frame_alloc();
    status = -5;
    if (!codec || !packet || !frame) goto cleanup;
    status = -1;
    if (avcodec_parameters_to_context(codec, parameters) < 0) goto cleanup;
    codec->thread_count = 1; codec->max_samples = 2 * MAX_RATE * MAX_CHANNELS;
    if (avcodec_open2(codec, decoder, NULL) < 0) goto cleanup;
    int read_status;
    while ((read_status = av_read_frame(format, packet)) >= 0) {
        if (timed_out(NULL)) { status = -6; goto cleanup; }
        if (packet->stream_index == stream) {
            int sent = avcodec_send_packet(codec, packet);
            if (sent < 0) { status = -1; goto cleanup; }
            status = drain(codec, frame, &resampler);
            if (status) goto cleanup;
        }
        av_packet_unref(packet);
    }
    if (read_status != AVERROR_EOF) { status = -1; goto cleanup; }
    if (avcodec_send_packet(codec, NULL) < 0) { status = -1; goto cleanup; }
    status = drain(codec, frame, &resampler);
    if (status) goto cleanup;
    if (resampler.swr) {
        uint8_t *dest[] = { (uint8_t *)(output + output_frames) };
        int count = swr_convert(resampler.swr, dest, MAX_FRAMES + 1 - output_frames, NULL, 0);
        if (count < 0) { status = -1; goto cleanup; }
        output_frames += count;
    }
    status = output_frames > MAX_FRAMES ? -3 : output_frames > 0 ? output_frames : -1;
cleanup:
    if (timed_out(NULL)) status = -6;
    av_frame_free(&frame); av_packet_free(&packet); avcodec_free_context(&codec);
    swr_free(&resampler.swr); av_channel_layout_uninit(&resampler.layout);
    avformat_close_input(&format);
    if (io) { av_freep(&io->buffer); avio_context_free(&io); }
    if (status < 0) output_frames = 0;
    return status;
}
